using Discord;
using Microsoft.Extensions.Logging;

namespace HambotV2.Connections
{
    /// <summary>
    /// The <see cref="IPresenceService"/> implementation (design §3.10): the single path for
    /// activity, bio, avatar, and banner changes over the gateway. All applies run under one
    /// single-flight lock — the last request wins; requests before the gateway is ready
    /// are recorded and applied on its Ready event together with the startup configuration;
    /// apply failures are logged and returned as false, never thrown.
    /// </summary>
    internal sealed class PresenceService : IPresenceService
    {
        private readonly IPresenceGateway _gateway;
        private readonly ILogger<PresenceService> _logger;
        private readonly SemaphoreSlim _updateLock = new(1, 1);

        /// <summary>
        /// The desired state, swapped atomically so the Current* properties are consistent
        /// lock-free reads. The managed flags distinguish "explicitly set or cleared" from
        /// "never managed": an unmanaged field is left untouched at startup.
        /// </summary>
        private ProfileState _state = new(null, null, null, null, false, false, false, false);

        private sealed record ProfileState(
            PresenceActivity? Activity,
            string? Bio,
            byte[]? Avatar,
            byte[]? Banner,
            bool ActivityManaged,
            bool BioManaged,
            bool AvatarManaged,
            bool BannerManaged);

        public PresenceService(IPresenceGateway gateway, PresenceStartupConfiguration? startupConfiguration, ILogger<PresenceService> logger)
        {
            _gateway = gateway;
            _logger = logger;

            if (startupConfiguration is not null)
            {
                _state = new ProfileState(
                    startupConfiguration.Activity,
                    startupConfiguration.HasBio ? startupConfiguration.Bio : null,
                    null,
                    null,
                    startupConfiguration.Activity is not null,
                    startupConfiguration.HasBio,
                    false,
                    false);
            }

            _gateway.Ready += OnGatewayReadyAsync;
        }

        public PresenceActivity? CurrentActivity => Volatile.Read(ref _state).Activity;

        public string? CurrentBio => Volatile.Read(ref _state).Bio;

        public string? CurrentAvatarUrl => _gateway.GetAvatarUrl();

        public string? CurrentBannerUrl => _gateway.GetBannerUrl();

        public async Task<bool> ApplyActivityAsync(ActivityType type, string text)
        {
            await _updateLock.WaitAsync();
            try
            {
                SetState(state => state with { Activity = new PresenceActivity(type, text), ActivityManaged = true });

                if (!_gateway.IsReady)
                    return true; // deferred until the gateway's Ready event

                return await TryApplyAsync(
                    () => _gateway.SetActivityAsync(type, text),
                    $"the activity '{PresenceActivityTypes.GetLabel(type)} {text}'");
            }
            finally
            {
                _updateLock.Release();
            }
        }

        public async Task<bool> ClearActivityAsync()
        {
            await _updateLock.WaitAsync();
            try
            {
                SetState(state => state with { Activity = null, ActivityManaged = true });

                if (!_gateway.IsReady)
                    return true;

                return await TryApplyAsync(() => _gateway.SetActivityAsync(ActivityType.Playing, null), "the activity clear");
            }
            finally
            {
                _updateLock.Release();
            }
        }

        public Task<bool> ApplyBioAsync(string bio) => SetBioAsync(bio);

        public Task<bool> ClearBioAsync() => SetBioAsync(null);

        public Task<bool> ApplyAvatarAsync(byte[] imageData) => SetAvatarAsync(imageData);

        public Task<bool> ResetAvatarAsync() => SetAvatarAsync(null);

        public Task<bool> ApplyBannerAsync(byte[] imageData) => SetBannerAsync(imageData);

        public Task<bool> ClearBannerAsync() => SetBannerAsync(null);

        private async Task<bool> SetBioAsync(string? bio)
        {
            await _updateLock.WaitAsync();
            try
            {
                SetState(state => state with { Bio = bio, BioManaged = true });

                if (!_gateway.IsReady)
                    return true;

                // The bio is per-guild state: one identity, expressed in every member guild.
                return await ApplyPerGuildAsync("the profile bio", guildId => _gateway.SetGuildBioAsync(guildId, bio));
            }
            finally
            {
                _updateLock.Release();
            }
        }

        private async Task<bool> SetAvatarAsync(byte[]? imageData)
        {
            await _updateLock.WaitAsync();
            try
            {
                SetState(state => state with { Avatar = imageData, AvatarManaged = true });

                if (!_gateway.IsReady)
                    return true;

                // Per-guild avatar plus the global avatar, so DMs and per-server profiles stay in sync.
                bool succeeded = await TryApplyAsync(() => _gateway.SetGlobalAvatarAsync(imageData), "the global avatar");
                succeeded &= await ApplyPerGuildAsync("the profile avatar", guildId => _gateway.SetGuildAvatarAsync(guildId, imageData));
                return succeeded;
            }
            finally
            {
                _updateLock.Release();
            }
        }

        private async Task<bool> SetBannerAsync(byte[]? imageData)
        {
            await _updateLock.WaitAsync();
            try
            {
                SetState(state => state with { Banner = imageData, BannerManaged = true });

                if (!_gateway.IsReady)
                    return true;

                // No global banner route for bots.
                return await ApplyPerGuildAsync("the profile banner", guildId => _gateway.SetGuildBannerAsync(guildId, imageData));
            }
            finally
            {
                _updateLock.Release();
            }
        }

        /// <summary>
        /// The Ready flush: the startup configuration and anything requested before the gateway
        /// was ready are applied under the same lock, so the flush cannot interleave with a
        /// concurrent runtime request.
        /// </summary>
        private async Task OnGatewayReadyAsync()
        {
            await _updateLock.WaitAsync();
            try
            {
                ProfileState state = Volatile.Read(ref _state);
                int failures = 0;

                if (state.ActivityManaged && state.Activity is not null)
                {
                    if (!await TryApplyAsync(() => _gateway.SetActivityAsync(state.Activity.Type, state.Activity.Text), "the startup activity"))
                        failures++;
                }

                if (state.BioManaged)
                {
                    if (!await ApplyPerGuildAsync("the startup bio", guildId => _gateway.SetGuildBioAsync(guildId, state.Bio)))
                        failures++;
                }

                if (state.AvatarManaged)
                {
                    if (!await TryApplyAsync(() => _gateway.SetGlobalAvatarAsync(state.Avatar), "the startup avatar"))
                        failures++;
                    if (!await ApplyPerGuildAsync("the startup avatar", guildId => _gateway.SetGuildAvatarAsync(guildId, state.Avatar)))
                        failures++;
                }

                if (state.BannerManaged)
                {
                    if (!await ApplyPerGuildAsync("the startup banner", guildId => _gateway.SetGuildBannerAsync(guildId, state.Banner)))
                        failures++;
                }

                if (failures == 0)
                    _logger.LogInformation("Startup presence and profile configuration applied.");
                else
                    _logger.LogWarning("Startup presence/profile apply had {FailureCount} failing part(s); see the logged errors.", failures);
            }
            finally
            {
                _updateLock.Release();
            }
        }

        /// <summary>Failures are logged, never thrown; the false return surfaces as the command's in-channel error.</summary>
        private async Task<bool> TryApplyAsync(Func<Task> apply, string description)
        {
            try
            {
                await apply();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply {Description} to Discord.", description);
                return false;
            }
        }

        /// <summary>Applies one field in every member guild; a failing guild is logged, the others are still attempted.</summary>
        private async Task<bool> ApplyPerGuildAsync(string description, Func<ulong, Task> apply)
        {
            bool allSucceeded = true;

            foreach (ulong guildId in _gateway.GetGuildIds())
            {
                if (!await TryApplyAsync(() => apply(guildId), $"{description} in guild {guildId}"))
                    allSucceeded = false;
            }

            return allSucceeded;
        }

        private void SetState(Func<ProfileState, ProfileState> update)
        {
            Volatile.Write(ref _state, update(Volatile.Read(ref _state)));
        }
    }
}
