using Discord;
using HambotV2.Connections;
using HambotV2.Llm;
using HambotV2.SlashCommands;

namespace HambotV2.Commands.SystemCommands
{
    /// <summary>
    /// Manages the invoking guild's bot settings. Guild overrides fall back to
    /// the global defaults from appsettings.json when unset. Admin-only by default, mirroring the
    /// permission-management commands; the bot owner can manage any guild by running the
    /// command there.
    /// </summary>
    internal class SettingsCommand : ICommand
    {
        private const int MaxIdentifierLength = 5;

        public string Name => "settings";
        public string Description => "Show or set this server's bot settings (identifier, cooldowns, persona, LLM chat context)";
        public string Usage => "settings [commandidentifier <value>|commandidentifier reset|cooldown <tier> <seconds>|cooldown <tier> reset|persona <text...>|persona clear|llmchat on|off]";
        public int DefaultPermissionTier => PermissionTier.Admin;
        public CommandCategory Category => CommandCategory.PermissionsAndSettings;

        /// <summary>
        /// Slash surface: the operation stays free-form — the subcommands
        /// (<c>commandidentifier</c>, <c>cooldown</c>) parse exactly as on the text surface.
        /// </summary>
        public ApplicationCommandDeclaration? ApplicationCommand =>
            ApplicationCommandDeclaration.FreeForm("Omit to show, or set: commandidentifier, cooldown, persona, llmchat");

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            // Guild settings are per-guild state; the DM scope has no guild to manage.
            if (context.GuildId is not ulong guildId)
            {
                await message.Channel.SendMessageAsync("This command manages per-server settings and is only available in servers");
                return;
            }

            var arguments = CommandArguments.Parse(message.Content);

            if (arguments.Count == 0)
            {
                await SendOverview(message, context, guildId);
                return;
            }

            switch (arguments.Get(0))
            {
                case "commandidentifier":
                    await HandleCommandIdentifier(message, context, guildId, arguments);
                    break;
                case "cooldown":
                    await HandleCooldown(message, context, guildId, arguments);
                    break;
                case "persona":
                    await HandlePersona(message, context, guildId, arguments);
                    break;
                case "llmchat":
                    await HandleLlmChat(message, context, guildId, arguments);
                    break;
                default:
                    await message.Channel.SendMessageAsync($"Unknown setting '{arguments.Get(0)}'. Usage: !{Usage}");
                    break;
            }
        }

        private async Task HandleCommandIdentifier(IMessage message, ICommandContext context, ulong guildId, CommandArguments arguments)
        {
            if (!arguments.TryGetString(1, out string action))
            {
                await message.Channel.SendMessageAsync($"Missing value. Usage: !{Usage}");
                return;
            }

            if (action == "reset")
            {
                context.GuildSettings.SetCommandIdentifier(guildId, null, context.DbContext);
                await context.DbContext.SaveChangesAsync();
                await message.Channel.SendMessageAsync("Command identifier reset to the global default");
                return;
            }

            if (action.Length > MaxIdentifierLength)
            {
                await message.Channel.SendMessageAsync($"The command identifier must be at most {MaxIdentifierLength} characters long");
                return;
            }

            context.GuildSettings.SetCommandIdentifier(guildId, action, context.DbContext);
            await context.DbContext.SaveChangesAsync();
            await message.Channel.SendMessageAsync($"Updated the command identifier for this server to {action}");
        }

        /// <summary>
        /// Manages the guild's per-tier cooldown override. The escalation guard mirrors
        ///: an invoker may only configure tiers strictly below their own, so nobody can
        /// loosen the throttle that governs their own level; the bot owner (tier 4, never bound
        /// by a guild override) may configure any tier.
        /// </summary>
        private async Task HandleCooldown(IMessage message, ICommandContext context, ulong guildId, CommandArguments arguments)
        {
            if (!arguments.TryGetInt(1, out int tier) || tier < PermissionTier.Everyone || tier > PermissionTier.Owner)
            {
                await message.Channel.SendMessageAsync($"Cooldowns can only be configured for tiers {PermissionTier.Everyone} to {PermissionTier.Owner}; the bot owner tier is global and never bound by a server. Usage: !{Usage}");
                return;
            }

            int invokerTier = context.GetUserEffectiveTier(message.Author.Id);
            if (invokerTier != PermissionTier.BotOwner && tier >= invokerTier)
            {
                await message.Channel.SendMessageAsync("You can only configure cooldowns for tiers below your own");
                return;
            }

            if (!arguments.TryGetString(2, out string action))
            {
                await message.Channel.SendMessageAsync($"Missing value. Usage: !{Usage}");
                return;
            }

            int? cooldownSeconds;
            if (action == "reset")
            {
                cooldownSeconds = null;
            }
            else if (arguments.TryGetInt(2, out int seconds) && seconds >= 0)
            {
                cooldownSeconds = seconds;
            }
            else
            {
                await message.Channel.SendMessageAsync("The cooldown must be a number of seconds of 0 or more, or 'reset'");
                return;
            }

            GetOrAddGuildSetting(context, guildId).SetCooldownOverride(tier, cooldownSeconds);
            await context.DbContext.SaveChangesAsync();

            await message.Channel.SendMessageAsync(cooldownSeconds is null
                ? $"Cooldown for tier {tier} in this server reset to the global default"
                : $"Cooldown for tier {tier} in this server set to {cooldownSeconds} seconds");
        }

        /// <summary>
        /// Manages the guild's LLM persona override: guild-scoped LLM runs use it ahead of the
        /// global chain; null means the guild inherits the global persona.
        /// </summary>
        private async Task HandlePersona(IMessage message, ICommandContext context, ulong guildId, CommandArguments arguments)
        {
            // Read path stays read-only: the row is only created when a value is actually set.
            var setting = context.DbContext.GuildSettings.FirstOrDefault(item => item.GuildId == guildId);

            if (arguments.Count == 1)
            {
                await message.Channel.SendMessageAsync(setting?.Persona is { } persona
                    ? $"Persona override for this server ({persona.Length} characters):\n{persona}"
                    : "Persona override: none set (this server inherits the global persona)");
                return;
            }

            if (arguments.Get(1) == "clear" && arguments.Count == 2)
            {
                if (setting is not null)
                    setting.Persona = null;
                await context.DbContext.SaveChangesAsync();
                await message.Channel.SendMessageAsync("Persona override cleared — this server inherits the global persona again");
                return;
            }

            string? text = arguments.GetJoined(1);
            if (string.IsNullOrWhiteSpace(text))
            {
                await message.Channel.SendMessageAsync($"The persona must not be empty. Usage: !{Usage}");
                return;
            }

            if (text.Length > LlmPersona.MaxPersonaLength)
            {
                await message.Channel.SendMessageAsync($"The persona must be at most {LlmPersona.MaxPersonaLength} characters (got {text.Length}).");
                return;
            }

            setting ??= GetOrAddGuildSetting(context, guildId);
            setting.Persona = text;
            await context.DbContext.SaveChangesAsync();
            await message.Channel.SendMessageAsync($"Persona override for this server set ({text.Length} characters)");
        }

        /// <summary>
        /// Opts this guild's chat into LLM prompts: when on, an LLM update triggered from this
        /// server includes recent messages from this server's readable channels only; off (the
        /// default) means no chat is read. Scheduled jobs and DM triggers never read chat.
        /// </summary>
        private async Task HandleLlmChat(IMessage message, ICommandContext context, ulong guildId, CommandArguments arguments)
        {
            var setting = context.DbContext.GuildSettings.FirstOrDefault(item => item.GuildId == guildId);

            if (arguments.Count == 1)
            {
                await message.Channel.SendMessageAsync(setting?.LlmIncludeChat == true
                    ? "LLM chat context: ON for this server — !llm update run here includes recent messages from this server's readable channels."
                    : "LLM chat context: OFF for this server (default) — no chat is read.");
                return;
            }

            if (!arguments.TryGetString(1, out string action) || (action != "on" && action != "off"))
            {
                await message.Channel.SendMessageAsync("Usage: !settings llmchat on|off");
                return;
            }

            GetOrAddGuildSetting(context, guildId).LlmIncludeChat = action == "on";
            await context.DbContext.SaveChangesAsync();
            await message.Channel.SendMessageAsync(action == "on"
                ? "LLM chat context enabled for this server: !llm update run here includes recent messages from this server's readable channels."
                : "LLM chat context disabled for this server — no chat is read for updates run here.");
        }

        private static DatabaseContext.GuildSetting GetOrAddGuildSetting(ICommandContext context, ulong guildId)
        {
            var setting = context.DbContext.GuildSettings.FirstOrDefault(item => item.GuildId == guildId);
            if (setting != null)
                return setting;

            setting = new DatabaseContext.GuildSetting { GuildId = guildId };
            context.DbContext.GuildSettings.Add(setting);
            return setting;
        }

        private static async Task SendOverview(IMessage message, ICommandContext context, ulong guildId)
        {
            var setting = context.DbContext.GuildSettings.FirstOrDefault(item => item.GuildId == guildId);

            if (setting?.CommandIdentifier != null)
                await message.Channel.SendMessageAsync($"Command identifier for this server: {setting.CommandIdentifier} (server override)");
            else
                await message.Channel.SendMessageAsync($"Command identifier: {context.CommandIdentifier} (global default)");

            var overrides = Enumerable.Range(PermissionTier.Everyone, PermissionTier.Owner + 1)
                .Select(tier => (Tier: tier, Seconds: setting?.GetCooldownOverride(tier)))
                .Where(entry => entry.Seconds != null)
                .Select(entry => $"tier {entry.Tier}: {entry.Seconds} seconds");

            await message.Channel.SendMessageAsync(overrides.Any()
                ? $"Cooldown overrides: {string.Join(", ", overrides)} (unset tiers use the global defaults)"
                : "Cooldown overrides: none set (all tiers use the global defaults)");

            await message.Channel.SendMessageAsync(setting?.Persona is { } persona
                ? $"Persona override: set ({persona.Length} characters)"
                : "Persona override: none set (inherits the global persona)");

            await message.Channel.SendMessageAsync(setting?.LlmIncludeChat == true
                ? "LLM chat context: ON (updates run in this server read this server's recent chat)"
                : "LLM chat context: OFF (no chat is read)");
        }
    }
}
