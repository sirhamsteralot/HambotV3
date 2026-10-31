using Discord;

using HambotV2.Connections;
using HambotV2.Llm;
using HambotV2.SlashCommands;

namespace HambotV2.Commands.LlmCommands
{
    /// <summary>
    /// Manages the global runtime persona override: no args show the effective global persona
    /// and which layer supplied it, <c>&lt;text...&gt;</c> sets the override (persisted in global
    /// storage), <c>clear</c> removes it so the configuration value or shipped seed applies again.
    /// Admin tier, DM-usable. The per-guild override lives on the <c>settings</c> command.
    /// </summary>
    internal sealed class PersonaCommand : ICommand
    {
        private readonly LlmSettings? _settings;

        public PersonaCommand(LlmSettings? settings = null)
        {
            _settings = settings;
        }

        public string Name => "persona";
        public string Description => "Shows, sets, or clears the bot's global LLM persona (per-server override: settings persona)";
        public string Usage => "persona [<text...> | clear]";
        public int DefaultPermissionTier => PermissionTier.Admin;
        public CommandCategory Category => CommandCategory.Llm;

        /// <summary>Slash surface: the persona text stays free-form, parsing as on the text surface.</summary>
        public ApplicationCommandDeclaration? ApplicationCommand =>
            ApplicationCommandDeclaration.FreeForm("Omit to show, <text...> to set the global persona, or clear");

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            var arguments = CommandArguments.Parse(message.Content);

            if (arguments.Count == 0)
            {
                (string persona, string layer) = LlmPersona.ResolveGlobalWithLayer(context.DbContext, _settings ?? new LlmSettings());
                await message.Channel.SendMessageAsync($"Global persona ({layer}):\n{persona}");
                return;
            }

            if (arguments.Get(0) == "clear" && arguments.Count == 1)
            {
                var existing = context.DbContext.BotSettings.FirstOrDefault(setting => setting.Key == LlmPersona.GlobalOverrideKey);
                if (existing is not null)
                    context.DbContext.BotSettings.Remove(existing);
                await context.DbContext.SaveChangesAsync();
                await message.Channel.SendMessageAsync("Global persona override cleared — the Llm:Persona configuration or the shipped default applies again.");
                return;
            }

            string? text = arguments.GetRaw();
            if (string.IsNullOrWhiteSpace(text))
            {
                await message.Channel.SendMessageAsync($"The persona must not be empty. Usage: {context.CommandIdentifier}{Usage}");
                return;
            }

            if (text.Length > LlmPersona.MaxPersonaLength)
            {
                await message.Channel.SendMessageAsync($"The persona must be at most {LlmPersona.MaxPersonaLength} characters (got {text.Length}).");
                return;
            }

            var setting = context.DbContext.BotSettings.FirstOrDefault(item => item.Key == LlmPersona.GlobalOverrideKey);
            if (setting is null)
            {
                setting = new DatabaseContext.BotSetting { Key = LlmPersona.GlobalOverrideKey };
                context.DbContext.BotSettings.Add(setting);
            }

            setting.Value = text;
            await context.DbContext.SaveChangesAsync();
            await message.Channel.SendMessageAsync($"Global persona override set ({text.Length} characters). It applies to every LLM run from the next tick.");
        }
    }
}
