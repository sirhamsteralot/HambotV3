using Discord;
using HambotV2.SlashCommands;

namespace HambotV2.Commands.SystemCommands
{
    /// <summary>
    /// Manages the tier the operator agent (the MCP endpoint) acts at in this
    /// guild: no args report it, <c>&lt;tier 0-4&gt;</c> raises it, <c>clear</c> resets it to the
    /// lowest-tier default. Owner tier by default — the guild owner or the bot owner — and the
    /// agent itself can never change the ceiling that governs it.
    /// </summary>
    internal sealed class AgentTierCommand : ICommand
    {
        public string Name => "agenttier";
        public string Description => "Shows, sets, or clears the tier the operator agent (the MCP integration) acts at in this server";
        public string Usage => "agenttier [<tier 0-4> | clear]";
        public int DefaultPermissionTier => PermissionTier.Owner;

        /// <summary>
        /// Slash surface: the argument stays free-form so <c>&lt;tier&gt;</c> and <c>clear</c>
        /// parse exactly as on the text surface.
        /// </summary>
        public ApplicationCommandDeclaration? ApplicationCommand =>
            ApplicationCommandDeclaration.FreeForm("Omit to show, or: a tier 0-4, or clear");

        public async Task ExecuteCommand(IMessage message, ICommandContext context)
        {
            // Guild settings are per-guild state; the DM scope has no guild to manage.
            if (context.GuildId is not ulong guildId)
            {
                await message.Channel.SendMessageAsync("This command manages per-server settings and is only available in servers");
                return;
            }

            // The ceiling governs the operator agent itself, so the agent must never change it —
            // only a real guild owner or bot owner may (run_command acts as the agent).
            if (message is VirtualCommandMessage { OperatorAgentUserId: not null })
            {
                await message.Channel.SendMessageAsync("The operator agent cannot change its own tier in this server");
                return;
            }

            var arguments = CommandArguments.Parse(message.Content);

            if (arguments.Count == 0)
            {
                await SendCurrentTierAsync(message, context, guildId);
                return;
            }

            string action = arguments.Get(0)!;
            int? newTier;
            if (action == "clear")
            {
                newTier = null;
            }
            else if (!int.TryParse(action, out int parsed) || !PermissionTier.IsValid(parsed))
            {
                await message.Channel.SendMessageAsync(
                    $"The tier must be a whole number {PermissionTier.Minimum} to {PermissionTier.Maximum}. Usage: {context.CommandIdentifier}{Usage}");
                return;
            }
            else
            {
                newTier = parsed;
            }

            context.GuildSettings.SetOperatorAgentTier(guildId, newTier, context.DbContext);
            await context.DbContext.SaveChangesAsync();

            await message.Channel.SendMessageAsync(newTier is null
                ? $"The operator agent tier was reset to the default (everyone, tier {PermissionTier.Everyone}) in this server"
                : $"The operator agent now acts at tier {newTier} ({PermissionTier.GetName(newTier.Value)}) in this server");
        }

        private static async Task SendCurrentTierAsync(IMessage message, ICommandContext context, ulong guildId)
        {
            var setting = await context.DbContext.GuildSettings.FindAsync(guildId);
            int? storedTier = setting?.OperatorAgentTier;

            await message.Channel.SendMessageAsync(storedTier is int tier
                ? $"The operator agent acts at tier {tier} ({PermissionTier.GetName(tier)}) in this server"
                : $"The operator agent acts at the default, tier {PermissionTier.Everyone} (everyone), in this server; raise it with a tier 1-{PermissionTier.Maximum}");
        }
    }
}
