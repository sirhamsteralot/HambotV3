using HambotV2.Connections;
using Microsoft.EntityFrameworkCore;

namespace HambotV2.LinkFix
{
    /// <summary>The outcome of one <c>!linkfixmap</c> write, as the command reports it.</summary>
    internal enum LinkMappingUpdateStatus
    {
        /// <summary>A row was written — a new origin added or an existing one repointed.</summary>
        Applied,

        /// <summary>An override was removed (the shipped default, if any, applies again).</summary>
        Cleared,

        /// <summary>There was no override to remove.</summary>
        NothingToClear,

        /// <summary>The origin is a mirror host of the effective mapping, so it may never be an origin.</summary>
        OriginIsMirrorHost,

        /// <summary>The mirror host is an origin of the effective mapping, so a rewrite would point back at it.</summary>
        MirrorIsKnownOrigin,

        /// <summary>The table is at <c>LinkFix:MaxMappings</c> and the origin is new.</summary>
        CapReached,

        /// <summary>An argument is not a bare hostname.</summary>
        InvalidHostname
    }

    /// <summary>
    /// The runtime layer of the origin→mirror mapping: the <c>LinkFixMapping</c> rows the bot
    /// owner writes with <c>!linkfixmap</c>, layered over the shipped defaults. This singleton is
    /// the only reader and the only writer of those rows; it holds one immutable snapshot, built
    /// on first use and replaced after every write, so a change applies from the next message
    /// with no restart and no cache to invalidate.
    /// </summary>
    internal interface ILinkMappingStore
    {
        /// <summary>The effective mapping (shipped defaults plus the current overrides).</summary>
        Task<LinkMapping> GetAsync(CancellationToken cancellationToken = default);

        /// <summary>Sets or overrides one origin→mirror row; the hostnames must already be normalized or normalizable.</summary>
        Task<LinkMappingUpdateStatus> SetAsync(string originHost, string mirrorHost, CancellationToken cancellationToken = default);

        /// <summary>Drops the override for an origin, so its shipped default (if any) applies again.</summary>
        Task<LinkMappingUpdateStatus> ClearAsync(string originHost, CancellationToken cancellationToken = default);
    }

    internal sealed class LinkMappingStore : ILinkMappingStore
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly LinkFixSettings _settings;
        private readonly ILogger<LinkMappingStore> _logger;

        /// <summary>Guards the first load, so concurrent messages read the table once.</summary>
        private readonly SemaphoreSlim _loadLock = new(1, 1);

        private LinkMapping? _snapshot;

        public LinkMappingStore(IServiceScopeFactory scopeFactory, LinkFixSettings settings, ILogger<LinkMappingStore> logger)
        {
            _scopeFactory = scopeFactory;
            _settings = settings;
            _logger = logger;
        }

        public async Task<LinkMapping> GetAsync(CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _snapshot) is { } cached)
                return cached;

            await _loadLock.WaitAsync(cancellationToken);
            try
            {
                if (Volatile.Read(ref _snapshot) is { } loaded)
                    return loaded;

                try
                {
                    var mapping = new LinkMapping(await ReadOverridesAsync(cancellationToken));
                    Volatile.Write(ref _snapshot, mapping);
                    return mapping;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The failure is not cached, so the next message retries; until then the
                    // shipped defaults keep the feature working.
                    _logger.LogError(ex, "Link mapping overrides could not be read; the shipped defaults apply until the next attempt.");
                    return LinkMapping.Shipped;
                }
            }
            finally
            {
                _loadLock.Release();
            }
        }

        public async Task<LinkMappingUpdateStatus> SetAsync(string originHost, string mirrorHost, CancellationToken cancellationToken = default)
        {
            if (!LinkHostNames.TryNormalize(originHost, out string? origin) || !LinkHostNames.TryNormalize(mirrorHost, out string? mirror))
                return LinkMappingUpdateStatus.InvalidHostname;

            LinkMapping current = await GetAsync(cancellationToken);
            if (current.IsKnownMirrorHost(origin))
                return LinkMappingUpdateStatus.OriginIsMirrorHost;

            if (current.IsKnownOrigin(mirror))
                return LinkMappingUpdateStatus.MirrorIsKnownOrigin;

            using IServiceScope scope = _scopeFactory.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<IDatabaseContext>();

            var row = await database.LinkFixMappings.FindAsync([origin], cancellationToken);

            // The cap bounds new origins only: an existing row still updates past it.
            if (row is null && !LinkMappingDefaults.TryGetEntry(origin, out _) && current.RowCount >= _settings.MaxMappings)
                return LinkMappingUpdateStatus.CapReached;

            if (row is null)
                database.LinkFixMappings.Add(new DatabaseContext.LinkFixMapping { OriginHost = origin, MirrorHost = mirror });
            else
                row.MirrorHost = mirror;

            await database.SaveChangesAsync(cancellationToken);
            await RefreshAsync(cancellationToken);
            return LinkMappingUpdateStatus.Applied;
        }

        public async Task<LinkMappingUpdateStatus> ClearAsync(string originHost, CancellationToken cancellationToken = default)
        {
            if (!LinkHostNames.TryNormalize(originHost, out string? origin))
                return LinkMappingUpdateStatus.InvalidHostname;

            using IServiceScope scope = _scopeFactory.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<IDatabaseContext>();

            var row = await database.LinkFixMappings.FindAsync([origin], cancellationToken);
            if (row is null)
                return LinkMappingUpdateStatus.NothingToClear;

            database.LinkFixMappings.Remove(row);
            await database.SaveChangesAsync(cancellationToken);
            await RefreshAsync(cancellationToken);
            return LinkMappingUpdateStatus.Cleared;
        }

        private async Task<List<KeyValuePair<string, string>>> ReadOverridesAsync(CancellationToken cancellationToken)
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<IDatabaseContext>();

            List<DatabaseContext.LinkFixMapping> rows = await database.LinkFixMappings.AsNoTracking().ToListAsync(cancellationToken);
            return rows.Select(row => new KeyValuePair<string, string>(row.OriginHost, row.MirrorHost)).ToList();
        }

        private async Task RefreshAsync(CancellationToken cancellationToken)
        {
            try
            {
                Volatile.Write(ref _snapshot, new LinkMapping(await ReadOverridesAsync(cancellationToken)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The row is already persisted; dropping the snapshot makes the next message load it.
                Volatile.Write(ref _snapshot, null);
                _logger.LogError(ex, "Link mapping overrides were written but could not be re-read; the next message reloads them.");
            }
        }
    }
}
