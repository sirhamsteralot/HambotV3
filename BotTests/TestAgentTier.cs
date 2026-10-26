using Discord;
using HambotV2.Commands;
using HambotV2.Commands.SystemCommands;
using HambotV2.Connections;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BotTests
{
    /// <summary>
    /// The per-guild operator-agent tier (design §3.11): the `!agenttier` command over
    /// the real guild-settings service, and the cached tier resolution the pipeline's
    /// run_command cap consumes — without a live database or Discord connection.
    /// </summary>
    [TestClass]
    public sealed class TestAgentTier
    {
        private const ulong GuildId = 5;
        private const ulong InvokerId = 200;

        /// <summary>A real GuildSettingsService over an isolated in-memory store, like the production singleton.</summary>
        private sealed class AgentTierHarness
        {
            public DatabaseContext Database { get; }
            public GuildSettingsService Service { get; }

            public AgentTierHarness()
            {
                var options = new DbContextOptionsBuilder<DatabaseContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString())
                    .Options;
                Database = new DatabaseContext(options);

                var configuration = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        { "BotConfiguration:CommandIdentifier", "!" }
                    })
                    .Build();

                var services = new ServiceCollection();
                services.AddScoped<IDatabaseContext>(_ => new DatabaseContext(options));
                Service = new GuildSettingsService(services.BuildServiceProvider(), configuration);
            }
        }

        private static Task<List<string>> ExecuteAsync(AgentTierHarness harness, string content, ulong? guildId = GuildId, IMessage? message = null)
        {
            var sentMessages = new List<string>();
            message ??= DiscordTestDoubles.CreateGuildMessage(content, InvokerId, 999, sentMessages, guildId ?? GuildId).Object;
            var context = DiscordTestDoubles.CreateContext(harness.Database, guildOwnerId: null, botOwnerId: null, guildId, harness.Service);

            return new AgentTierCommand().ExecuteCommand(message, context).ContinueWith(
                _ => sentMessages, TaskScheduler.Default);
        }

        [TestMethod]
        public async Task Show_UnsetReportsTheLowestTierDefault()
        {
            var harness = new AgentTierHarness();

            List<string> replies = await ExecuteAsync(harness, "!agenttier");

            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], $"default, tier {PermissionTier.Everyone} (everyone)");
            Assert.AreEqual(PermissionTier.Everyone, harness.Service.GetOperatorAgentTier(GuildId), "Unset resolves to the lowest tier.");
        }

        [TestMethod]
        public async Task Set_PersistsAndRefreshesTheCache()
        {
            var harness = new AgentTierHarness();

            List<string> replies = await ExecuteAsync(harness, "!agenttier 2");

            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], $"tier 2 ({PermissionTier.GetName(PermissionTier.Admin)})");
            Assert.AreEqual(PermissionTier.Admin, harness.Service.GetOperatorAgentTier(GuildId), "The cache sees the new tier immediately.");
            Assert.AreEqual(PermissionTier.Admin, harness.Database.GuildSettings.Single().OperatorAgentTier, "The tier persists in the guild's settings row.");
        }

        [TestMethod]
        public async Task Set_UpdatesAnExistingSettingsRowInPlace()
        {
            var harness = new AgentTierHarness();
            harness.Database.GuildSettings.Add(new DatabaseContext.GuildSetting { GuildId = GuildId, CommandIdentifier = "?" });
            harness.Database.SaveChanges();

            await ExecuteAsync(harness, "!agenttier 1");

            var setting = harness.Database.GuildSettings.Single();
            Assert.AreEqual("?", setting.CommandIdentifier, "The identifier override survives the tier write.");
            Assert.AreEqual(PermissionTier.Moderator, setting.OperatorAgentTier);
            Assert.AreEqual("?", harness.Service.GetCommandIdentifier(GuildId), "The identifier cache is untouched by the tier write.");
        }

        [TestMethod]
        public async Task Clear_ResetsToTheLowestTierDefault()
        {
            var harness = new AgentTierHarness();
            await ExecuteAsync(harness, "!agenttier 2");

            List<string> replies = await ExecuteAsync(harness, "!agenttier clear");

            StringAssert.Contains(replies[0], $"reset to the default (everyone, tier {PermissionTier.Everyone})");
            Assert.IsNull(harness.Database.GuildSettings.Single().OperatorAgentTier);
            Assert.AreEqual(PermissionTier.Everyone, harness.Service.GetOperatorAgentTier(GuildId));
        }

        [TestMethod]
        public async Task Set_RejectsInvalidTiers()
        {
            var harness = new AgentTierHarness();

            foreach (string argument in new[] { "5", "-1", "two" })
            {
                List<string> replies = await ExecuteAsync(harness, $"!agenttier {argument}");
                Assert.AreEqual(1, replies.Count, $"The argument '{argument}' gets exactly one error reply.");
                StringAssert.Contains(replies[0], $"whole number {PermissionTier.Minimum} to {PermissionTier.Maximum}");
            }

            Assert.AreEqual(0, harness.Database.GuildSettings.Count(), "Nothing is persisted on invalid input.");
        }

        [TestMethod]
        public async Task DirectMessage_Declines()
        {
            var harness = new AgentTierHarness();

            List<string> replies = await ExecuteAsync(harness, "!agenttier 2", guildId: null);

            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], "only available in servers");
            Assert.AreEqual(0, harness.Database.GuildSettings.Count(), "No per-guild state is written from direct messages.");
        }

        [TestMethod]
        public async Task OperatorAgentInvocation_Declines()
        {
            var harness = new AgentTierHarness();
            var channel = new VirtualCommandChannel(new Mock<IMessageChannel>().Object);
            var message = new VirtualCommandMessage(
                DiscordTestDoubles.CreateUser(InvokerId).Object, channel, "!agenttier 4", DateTimeOffset.UnixEpoch,
                operatorAgentUserId: InvokerId);

            await new AgentTierCommand().ExecuteCommand(
                message, DiscordTestDoubles.CreateContext(harness.Database, guildId: GuildId, guildSettings: harness.Service));

            Assert.AreEqual(1, channel.SentMessages.Count, "The agent gets exactly one reply.");
            StringAssert.Contains(channel.SentMessages[0].Text, "cannot change its own tier");
            Assert.IsNull(harness.Database.GuildSettings.SingleOrDefault()?.OperatorAgentTier, "The agent cannot change the ceiling that governs it.");
        }

        [TestMethod]
        public void Command_IsOwnerTierAndRegistered()
        {
            var commandService = new CommandService();
            commandService.RegisterCommands();

            ICommand command = commandService.Commands["agenttier"];
            Assert.AreEqual(PermissionTier.Owner, command.DefaultPermissionTier, "Only the guild owner or the bot owner may change the agent's tier.");
        }

        [TestMethod]
        public void Service_CacheIsPerGuild()
        {
            var harness = new AgentTierHarness();
            harness.Service.SetOperatorAgentTier(GuildId, PermissionTier.Moderator, harness.Database);
            harness.Database.SaveChanges();

            Assert.AreEqual(PermissionTier.Moderator, harness.Service.GetOperatorAgentTier(GuildId));
            Assert.AreEqual(PermissionTier.Everyone, harness.Service.GetOperatorAgentTier(GuildId + 1), "Other guilds keep the lowest-tier default.");
        }
    }
}
