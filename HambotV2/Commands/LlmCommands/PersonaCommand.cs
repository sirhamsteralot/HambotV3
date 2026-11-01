using Discord;

using HambotV2.Connections;
using HambotV2.Llm;
using HambotV2.SlashCommands;

namespace HambotV2.Commands.LlmCommands
{
    /// <summary>
    /// The LLM persona: no args shows the effective persona for the current scope (in a guild
    /// the server override plus the global chain, in DMs the global chain);
    /// <c>global [<text...>|clear]</c> manages the persisted global runtime override (DM-usable);
    /// <c>server [<text...>|clear]</c> manages this guild's persisted override (guild-only).
    /// Admin tier.
    /// </summary>
    internal sealed class PersonaCommand : ICommand
    {
        private readonly LlmSettings? _settings;

        public PersonaCommand(LlmSettings? settings = null)
        {
            _settings = settings;
        }

        public string Name => "persona";
        public string Description => "Shows or sets the LLM persona (global override or per-server)";
        public string Usage => "persona [global|server [<text...>|clear]]";
        public int DefaultPermissionTier => PermissionTier.Admin;
        public CommandCategory Category => CommandCategory.Llm;

        /// <summary>Slash surface: the scope stays free-form, parsing as on the text surface.</summary>
        public ApplicationCommandDeclaration? ApplicationCommand =>
            ApplicationCommandDeclaration.FreeForm("Omit to show the current scope, or: global [<text...>|clear], server [<text...>|clear]");

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            var arguments = CommandArguments.Parse(message.Content);
            var settings = _settings ?? new LlmSettings();

            switch (arguments.Get(0))
            {
                case null:
                    await SendPersonaOverviewAsync(message, context, settings);
                    return;
                case "global":
                    await HandleGlobalPersonaAsync(message, context, settings, arguments);
                    return;
                case "server":
                    await HandleServerPersonaAsync(message, context, arguments);
                    return;
                default:
                    await message.Channel.SendMessageAsync($"Unknown persona scope '{arguments.Get(0)}'. Usage: {context.CommandIdentifier}{Usage}");
                    return;
            }
        }

        /// <summary>Bare <c>persona</c>: the effective persona for the current scope — in a guild the server override plus the global chain, in DMs the global chain.</summary>
        private static async Task SendPersonaOverviewAsync(IMessage message, ICommandContext context, LlmSettings settings)
        {
            if (context.GuildId is ulong guildId)
            {
                string? serverPersona = context.DbContext.GuildSettings.FirstOrDefault(item => item.GuildId == guildId)?.Persona;
                await message.Channel.SendMessageAsync(!string.IsNullOrWhiteSpace(serverPersona)
                    ? $"Persona for this server (server override, {serverPersona.Length} characters):\n{serverPersona}"
                    : "Persona for this server: no server override — the global persona applies (set one with 'persona server <text...>')");
            }

            (string global, string layer) = LlmPersona.ResolveGlobalWithLayer(context.DbContext, settings);
            await message.Channel.SendMessageAsync($"Global persona ({layer}):\n{global}");
        }

        private static async Task HandleGlobalPersonaAsync(IMessage message, ICommandContext context, LlmSettings settings, CommandArguments arguments)
        {
            if (arguments.Count == 1)
            {
                (string persona, string layer) = LlmPersona.ResolveGlobalWithLayer(context.DbContext, settings);
                await message.Channel.SendMessageAsync($"Global persona ({layer}):\n{persona}");
                return;
            }

            if (arguments.Get(1) == "clear" && arguments.Count == 2)
            {
                await BotSettingOverride.ClearAsync(context.DbContext, LlmPersona.GlobalOverrideKey);
                await message.Channel.SendMessageAsync("Global persona override cleared — the Llm:Persona configuration or the shipped default applies again.");
                return;
            }

            string? text = arguments.GetRawFrom(1);
            if (string.IsNullOrWhiteSpace(text))
            {
                await message.Channel.SendMessageAsync($"The persona must not be empty. Usage: {context.CommandIdentifier}persona global [<text...>|clear]");
                return;
            }

            if (text.Length > LlmPersona.MaxPersonaLength)
            {
                await message.Channel.SendMessageAsync($"The persona must be at most {LlmPersona.MaxPersonaLength} characters (got {text.Length}).");
                return;
            }

            await BotSettingOverride.SetAsync(context.DbContext, LlmPersona.GlobalOverrideKey, text);
            await message.Channel.SendMessageAsync($"Global persona override set ({text.Length} characters). It applies to every LLM run from the next tick.");
        }

        private static async Task HandleServerPersonaAsync(IMessage message, ICommandContext context, CommandArguments arguments)
        {
            // The server scope is per-guild state; the DM scope has no guild to manage.
            if (context.GuildId is not ulong guildId)
            {
                await message.Channel.SendMessageAsync("The 'persona server' scope manages per-server state and is only available in servers — use 'persona global' in DMs.");
                return;
            }

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

            string? text = arguments.GetRawFrom(1);
            if (string.IsNullOrWhiteSpace(text))
            {
                await message.Channel.SendMessageAsync($"The persona must not be empty. Usage: {context.CommandIdentifier}persona server [<text...>|clear]");
                return;
            }

            if (text.Length > LlmPersona.MaxPersonaLength)
            {
                await message.Channel.SendMessageAsync($"The persona must be at most {LlmPersona.MaxPersonaLength} characters (got {text.Length}).");
                return;
            }

            setting ??= await GuildSettingStore.GetOrAddAsync(context.DbContext, guildId);
            setting.Persona = text;
            await context.DbContext.SaveChangesAsync();
            await message.Channel.SendMessageAsync($"Persona override for this server set ({text.Length} characters)");
        }
    }
}
