using System.Diagnostics;
using Npgsql;

namespace HambotV2.Backups
{
    /// <summary>One dump file in the shared backup volume: name, timestamp, and size in bytes.</summary>
    internal sealed record BackupInfo(string Name, DateTimeOffset Timestamp, long SizeBytes);

    /// <summary>
    /// Backup management over the shared backup volume (design §5.2): the scheduled
    /// sidecar and the in-bot commands operate on the same directory, so on-demand dumps land
    /// next to the scheduled ones and both are visible to `!backups`. The process boundary keeps
    /// the commands pure orchestration — the implementation shells out to pg_dump/pg_restore,
    /// so unit tests fake this interface.
    /// </summary>
    internal interface IBackupManager
    {
        /// <summary>
        /// Creates a timestamped compressed custom-format dump (<c>pg_dump -Fc</c>) in the shared
        /// directory — the same format and location as the scheduled backups — validates it like
        /// the sidecar does (restore-catalog listing + size floor) and returns its name and size.
        /// Throws on any failure; an invalid dump is removed again.
        /// </summary>
        Task<BackupInfo> CreateBackupAsync(CancellationToken cancellationToken = default);

        /// <summary>The dump files in the shared directory, newest first.</summary>
        IReadOnlyList<BackupInfo> ListBackups();

        /// <summary>
        /// Restores the named dump into the live database (<c>pg_restore --clean --if-exists</c>,
        /// dropping and recreating existing objects). Throws on any failure.
        /// </summary>
        Task RestoreBackupAsync(string name, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Runs pg_dump/pg_restore in-process against the shared backup volume (design §5.2). The
    /// runtime image installs postgresql-client and compose mounts the sidecar's `backups`
    /// volume into the bot, so this reaches the same files the scheduled loop produces.
    /// </summary>
    internal sealed class ProcessBackupManager : IBackupManager
    {
        /// <summary>Minimum size a valid dump must reach (matches the sidecar's size-floor default).</summary>
        internal const long MinimumDumpSizeBytes = 1024;

        internal const string DumpFilePrefix = "hambotv2-";
        internal const string DumpFileSuffix = ".dump";

        private readonly string _backupDirectory;
        private readonly string _postgresUri;
        private readonly ILogger<ProcessBackupManager> _logger;

        public ProcessBackupManager(IConfiguration configuration, ILogger<ProcessBackupManager> logger)
        {
            _backupDirectory = configuration["Backups:Directory"] ?? "backups";
            _postgresUri = BuildPostgresUri(configuration.GetConnectionString("DefaultConnection") ?? string.Empty);
            _logger = logger;
        }

        /// <summary>
        /// The bot's Npgsql connection string ("Server=…;User Id=…") is not valid libpq
        /// syntax — pg_dump/pg_restore would reject its keywords — so the pg tools get an
        /// equivalent connection URI instead. User, password, and database name are
        /// percent-encoded so special characters survive the URI grammar.
        /// </summary>
        internal static string BuildPostgresUri(string connectionString)
        {
            var connection = new NpgsqlConnectionStringBuilder(connectionString);
            string credentials = string.IsNullOrEmpty(connection.Username)
                ? string.Empty
                : $"{Uri.EscapeDataString(connection.Username)}:{Uri.EscapeDataString(connection.Password ?? string.Empty)}@";
            return $"postgresql://{credentials}{connection.Host}:{connection.Port}/{Uri.EscapeDataString(connection.Database ?? string.Empty)}";
        }

        public async Task<BackupInfo> CreateBackupAsync(CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(_backupDirectory);

            var timestamp = DateTimeOffset.UtcNow;
            string fileName = $"{DumpFilePrefix}{timestamp:yyyyMMdd-HHmmss}{DumpFileSuffix}";
            string path = Path.Combine(_backupDirectory, fileName);

            // The pg tools take the connection as a URI (see BuildPostgresUri) with --dbname.
            await RunAsync("pg_dump",
                new[] { "--format=custom", $"--file={fileName}", $"--dbname={_postgresUri}" },
                cancellationToken);

            long size = new FileInfo(path).Length;
            if (size < MinimumDumpSizeBytes || !await CanListRestoreCatalogAsync(path, cancellationToken))
            {
                // A failed dump counts for nothing: remove it so it never shows up in
                // !backups or the retention window as if it were good.
                TryDelete(path);
                _logger.LogError("The on-demand backup {BackupName} failed validation (restore catalog or size floor; size {SizeBytes} bytes).", fileName, size);
                throw new InvalidOperationException("The created dump failed validation (restore catalog or size floor).");
            }

            _logger.LogInformation("On-demand backup created: {BackupName} ({SizeBytes} bytes)", fileName, size);
            return new BackupInfo(fileName, timestamp, size);
        }

        public IReadOnlyList<BackupInfo> ListBackups()
        {
            if (!Directory.Exists(_backupDirectory))
                return Array.Empty<BackupInfo>();

            return Directory.EnumerateFiles(_backupDirectory, $"{DumpFilePrefix}*{DumpFileSuffix}")
                .Select(path =>
                {
                    var info = new FileInfo(path);
                    return new BackupInfo(info.Name, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), info.Length);
                })
                .OrderByDescending(backup => backup.Timestamp)
                .ToList();
        }

        public async Task RestoreBackupAsync(string name, CancellationToken cancellationToken = default)
        {
            string path = Path.Combine(_backupDirectory, name);
            if (!File.Exists(path))
                throw new FileNotFoundException($"No backup named '{name}' exists.", path);

            // --clean --if-exists: existing objects are dropped before the dump's contents are
            // restored, so the restored database matches the dump.
            await RunAsync("pg_restore",
                new[] { "--clean", "--if-exists", $"--dbname={_postgresUri}", path },
                cancellationToken);

            _logger.LogInformation("Backup {BackupName} restored into the live database", name);
        }

        /// <summary>The dump's restore catalog must be listable.</summary>
        private async Task<bool> CanListRestoreCatalogAsync(string path, CancellationToken cancellationToken)
        {
            try
            {
                await RunAsync("pg_restore", new[] { "--list", path }, cancellationToken);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Restore-catalog listing failed for {BackupName}", Path.GetFileName(path));
                return false;
            }
        }

        private void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not remove the invalid dump {BackupName}", Path.GetFileName(path));
            }
        }

        /// <summary>Runs a pg tool in the backup directory; a non-zero exit code throws.</summary>
        private async Task RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = _backupDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            if (process is null)
                throw new InvalidOperationException($"Could not start {fileName}.");

            string stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
            {
                // The commands reply generically: no exception details in Discord.
                // so this line is where the actual pg tool error lands.
                _logger.LogError("{Tool} failed with exit code {ExitCode}: {Error}", fileName, process.ExitCode, stderr.Trim());
                throw new InvalidOperationException($"{fileName} failed (exit {process.ExitCode}): {stderr.Trim()}");
            }
        }
    }

    /// <summary>Formats byte counts for the backup command replies.</summary>
    internal static class BackupSize
    {
        public static string Format(long bytes) => bytes >= 1024 * 1024
            ? $"{bytes / (double)(1024 * 1024):0.##} MB"
            : bytes >= 1024
                ? $"{bytes / 1024.0:0.##} KB"
                : $"{bytes} B";
    }
}
