using System.Text.Json;

namespace HambotV2.Backups
{
    /// <summary>A successful backup recorded by the sidecar: the dump's name, completion time, and size.</summary>
    internal sealed record BackupStatusEntry(string Name, DateTimeOffset Timestamp, long SizeBytes);

    /// <summary>A failed dump validation: when it happened and why the dump was rejected.</summary>
    internal sealed record BackupValidationError(DateTimeOffset Timestamp, string Message);

    /// <summary>The outcome of one automated restore test.</summary>
    internal sealed record RestoreTestResult(DateTimeOffset Timestamp, bool Succeeded, string Detail);

    /// <summary>
    /// The backup sidecar's status file (<c>backups/.backup-status</c>, design §5.2/§3.13): the single
    /// channel through which the in-bot health surface sees the backup machinery — written by
    /// <c>scripts/backup/run-backup.sh</c> after every pass, read here for `!health` and the
    /// watchdog. The interval fields carry the sidecar's own schedule so the watchdog can
    /// decide "overdue" without duplicating the compose configuration.
    /// </summary>
    internal sealed record BackupStatus(
        BackupStatusEntry? LastSuccessfulBackup,
        BackupValidationError? LastValidationError,
        RestoreTestResult? LastRestoreTest,
        int? BackupIntervalSeconds,
        int? RestoreTestIntervalSeconds,
        DateTimeOffset UpdatedAt);

    /// <summary>Reads the shared backup status file; null when no sidecar has written one (or it is unreadable).</summary>
    internal interface IBackupStatusReader
    {
        BackupStatus? Read();
    }

    internal sealed class BackupStatusFileReader : IBackupStatusReader
    {
        internal const string StatusFileName = ".backup-status";

        // The sidecar writes camelCase JSON (jq); the web defaults read it case-insensitively.
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly string _backupDirectory;
        private readonly ILogger<BackupStatusFileReader> _logger;

        public BackupStatusFileReader(IConfiguration configuration, ILogger<BackupStatusFileReader> logger)
        {
            _backupDirectory = configuration["Backups:Directory"] ?? "backups";
            _logger = logger;
        }

        public BackupStatus? Read()
        {
            string path = Path.Combine(_backupDirectory, StatusFileName);
            if (!File.Exists(path))
                return null;

            try
            {
                string json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<BackupStatus>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The backup status file {StatusFile} exists but could not be read", path);
                return null;
            }
        }
    }
}
