using HambotV2.Commands;
using HambotV2.Commands.SystemCommands;
using HambotV2.Connections;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BotTests
{
    /// <summary>
    /// Guild-scoped settings (FR-26): per-guild identifier resolution with global
    /// fallback, runtime writes with cache refresh, and the !settings command managing them.
    /// </summary>
    [TestClass]
    public sealed class TestGuildSettings
    {
        private const ulong GuildId = 5;

        /// <summary>
        /// A real GuildSettingsService over an isolated in-memory store. Each scope gets its own
        /// context over the same store (like the transient contexts in production), so disposing a
        /// scope never disposes the test-owned context.
        /// </summary>
        private sealed class SettingsHarness
        {
            public DatabaseContext Database { get; }
            public GuildSettingsService Service { get; }

            public SettingsHarness(string globalIdentifier = "!")
            {
                var options = new DbContextOptionsBuilder<DatabaseContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString())
                    .Options;
                Database = new DatabaseContext(options);

                var configuration = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        { "BotConfiguration:CommandIdentifier", globalIdentifier }
                    })
                    .Build();

                var services = new ServiceCollection();
                services.AddScoped<IDatabaseContext>(_ => new DatabaseContext(options));
                Service = new GuildSettingsService(services.BuildServiceProvider(), configuration);
            }
        }

        private static async Task<List<string>> ExecuteSettings(SettingsHarness harness, string content, ulong? guildId = GuildId, int? invokerTier = null, ulong? botOwnerId = null)
        {
            if (invokerTier is int tier)
            {
                // Seed the invoking user's stored tier (author id 200) when a test needs an elevated invoker.
                harness.Database.Users.Add(new DatabaseContext.User { GuildId = guildId ?? GuildId, DiscordId = 200, PermissionTier = tier });
                harness.Database.SaveChanges();
            }

            var sentMessages = new List<string>();
            var message = DiscordTestDoubles.CreateGuildMessage(content, 200, 999, sentMessages);
            var context = DiscordTestDoubles.CreateContext(harness.Database, guildOwnerId: null, botOwnerId: botOwnerId, guildId, harness.Service);

            await new SettingsCommand().ExecuteCommand(message.Object, context);
            return sentMessages;
        }

        [TestMethod]
        public void GetCommandIdentifier_NoOverride_FallsBackToGlobalDefault()
        {
            var harness = new SettingsHarness(globalIdentifier: "!");

            Assert.AreEqual("!", harness.Service.GetCommandIdentifier(GuildId));
            Assert.AreEqual("!", harness.Service.GlobalCommandIdentifier);
        }

        [TestMethod]
        public void GetCommandIdentifier_GuildOverride_Wins_While_Other_Guilds_Use_Default()
        {
            var harness = new SettingsHarness(globalIdentifier: "!");
            harness.Database.GuildSettings.Add(new DatabaseContext.GuildSetting { GuildId = GuildId, CommandIdentifier = "?" });
            harness.Database.SaveChanges();

            Assert.AreEqual("?", harness.Service.GetCommandIdentifier(GuildId));
            Assert.AreEqual("!", harness.Service.GetCommandIdentifier(77), "Guilds without an override must fall back to the global default");
        }

        [TestMethod]
        public void SetCommandIdentifier_Creates_Overrides_And_Refreshes_The_Cache()
        {
            var harness = new SettingsHarness();

            harness.Service.SetCommandIdentifier(GuildId, "?", harness.Database);
            harness.Database.SaveChanges();

            var row = harness.Database.GuildSettings.Single(setting => setting.GuildId == GuildId);
            Assert.AreEqual("?", row.CommandIdentifier);
            Assert.AreEqual("?", harness.Service.GetCommandIdentifier(GuildId), "The write must refresh the cache immediately");
        }

        [TestMethod]
        public void SetCommandIdentifier_Updates_Existing_Row_And_Reset_Falls_Back()
        {
            var harness = new SettingsHarness();
            harness.Service.SetCommandIdentifier(GuildId, "?", harness.Database);
            harness.Database.SaveChanges();

            harness.Service.SetCommandIdentifier(GuildId, "??", harness.Database);
            harness.Database.SaveChanges();
            Assert.AreEqual("??", harness.Database.GuildSettings.Single(setting => setting.GuildId == GuildId).CommandIdentifier);

            harness.Service.SetCommandIdentifier(GuildId, null, harness.Database);
            harness.Database.SaveChanges();

            Assert.IsNull(harness.Database.GuildSettings.Single(setting => setting.GuildId == GuildId).CommandIdentifier);
            Assert.AreEqual("!", harness.Service.GetCommandIdentifier(GuildId), "A cleared override must fall back to the global default");
        }

        [TestMethod]
        public void Guild_Identifier_Override_Changes_Command_Detection()
        {
            // FR-26 end to end: the guild's effective identifier decides which messages are commands.
            var harness = new SettingsHarness(globalIdentifier: "!");
            var commandService = new CommandService();
            commandService.RegisterCommands();

            harness.Service.SetCommandIdentifier(GuildId, "?", harness.Database);
            harness.Database.SaveChanges();

            string guildIdentifier = harness.Service.GetCommandIdentifier(GuildId);
            Assert.AreEqual("?", guildIdentifier);
            Assert.IsTrue(commandService.TryGetCommand("?hello", guildIdentifier, out ICommand? overridden));
            Assert.AreEqual("hello", overridden.Name);
            Assert.IsFalse(commandService.TryGetCommand("!hello", guildIdentifier, out _), "The old global identifier must no longer trigger commands in the overriding guild");

            string otherGuildIdentifier = harness.Service.GetCommandIdentifier(77);
            Assert.IsTrue(commandService.TryGetCommand("!hello", otherGuildIdentifier, out _), "Guilds without an override keep the global identifier");
        }

        [TestMethod]
        public async Task Settings_Overview_Shows_Global_Default_When_No_Override()
        {
            var harness = new SettingsHarness();

            var replies = await ExecuteSettings(harness, "!settings");

            StringAssert.Contains(replies[0], "Command identifier: ! (global default)");
        }

        [TestMethod]
        public async Task Settings_Overview_Shows_Server_Override()
        {
            var harness = new SettingsHarness();
            harness.Database.GuildSettings.Add(new DatabaseContext.GuildSetting { GuildId = GuildId, CommandIdentifier = "?" });
            harness.Database.SaveChanges();

            var replies = await ExecuteSettings(harness, "!settings");

            StringAssert.Contains(replies[0], "Command identifier for this server: ? (server override)");
        }

        [TestMethod]
        public async Task Settings_SetIdentifier_Persists_Override()
        {
            var harness = new SettingsHarness();

            var replies = await ExecuteSettings(harness, "!settings commandidentifier ?");

            StringAssert.Contains(replies[0], "Updated the command identifier for this server to ?");
            Assert.AreEqual("?", harness.Database.GuildSettings.Single(setting => setting.GuildId == GuildId).CommandIdentifier);
            Assert.AreEqual("?", harness.Service.GetCommandIdentifier(GuildId));
        }

        [TestMethod]
        public async Task Settings_ResetIdentifier_Clears_Override()
        {
            var harness = new SettingsHarness();
            harness.Service.SetCommandIdentifier(GuildId, "?", harness.Database);
            harness.Database.SaveChanges();

            var replies = await ExecuteSettings(harness, "!settings commandidentifier reset");

            StringAssert.Contains(replies[0], "Command identifier reset to the global default");
            Assert.IsNull(harness.Database.GuildSettings.Single(setting => setting.GuildId == GuildId).CommandIdentifier);
            Assert.AreEqual("!", harness.Service.GetCommandIdentifier(GuildId));
        }

        [TestMethod]
        public async Task Settings_Identifier_TooLong_Is_Rejected()
        {
            var harness = new SettingsHarness();

            var replies = await ExecuteSettings(harness, "!settings commandidentifier ??????");

            StringAssert.Contains(replies[0], "at most 5 characters");
            Assert.AreEqual(0, harness.Database.GuildSettings.Count());
        }

        [TestMethod]
        public async Task Settings_Unknown_Setting_Shows_Usage()
        {
            var harness = new SettingsHarness();

            var replies = await ExecuteSettings(harness, "!settings color red");

            StringAssert.Contains(replies[0], "Unknown setting 'color'");
            StringAssert.Contains(replies[0], "Usage: !settings");
        }

        [TestMethod]
        public async Task Settings_Missing_Value_Shows_Usage()
        {
            var harness = new SettingsHarness();

            var replies = await ExecuteSettings(harness, "!settings commandidentifier");

            StringAssert.Contains(replies[0], "Missing value");
            Assert.AreEqual(0, harness.Database.GuildSettings.Count());
        }

        [TestMethod]
        public async Task Settings_InDirectMessage_Is_Refused_Without_Writes()
        {
            // DM scope (FR-25): guild settings are per-server state; the command must decline in
            // direct messages without touching the database.
            var harness = new SettingsHarness();

            var replies = await ExecuteSettings(harness, "!settings commandidentifier ?", guildId: null);

            StringAssert.Contains(replies[0], "only available in servers");
            Assert.AreEqual(0, harness.Database.GuildSettings.Count(), "No state may be written from a direct message");
        }

        [TestMethod]
        public async Task Settings_SetCooldown_PersistsOverride()
        {
            var harness = new SettingsHarness();

            var replies = await ExecuteSettings(harness, "!settings cooldown 0 5", invokerTier: PermissionTier.Admin);

            StringAssert.Contains(replies[0], "Cooldown for tier 0 in this server set to 5 seconds");
            Assert.AreEqual(5, harness.Database.GuildSettings.Single(setting => setting.GuildId == GuildId).CooldownSecondsTier0);
        }

        [TestMethod]
        public async Task Settings_SetCooldownZeroSeconds_IsAllowed()
        {
            // A window of 0 disables throttling for that tier.
            var harness = new SettingsHarness();

            var replies = await ExecuteSettings(harness, "!settings cooldown 1 0", invokerTier: PermissionTier.Admin);

            StringAssert.Contains(replies[0], "set to 0 seconds");
            Assert.AreEqual(0, harness.Database.GuildSettings.Single(setting => setting.GuildId == GuildId).CooldownSecondsTier1);
        }

        [TestMethod]
        public async Task Settings_CooldownReset_ClearsOverride()
        {
            var harness = new SettingsHarness();
            harness.Database.GuildSettings.Add(new DatabaseContext.GuildSetting { GuildId = GuildId, CooldownSecondsTier1 = 5 });
            harness.Database.SaveChanges();

            var replies = await ExecuteSettings(harness, "!settings cooldown 1 reset", invokerTier: PermissionTier.Admin);

            StringAssert.Contains(replies[0], "Cooldown for tier 1 in this server reset to the global default");
            Assert.IsNull(harness.Database.GuildSettings.Single(setting => setting.GuildId == GuildId).CooldownSecondsTier1);
        }

        [TestMethod]
        public async Task Settings_CooldownTierOutsideScale_IsRejected()
        {
            // Configurable tiers are 0–3; tier 4 is global and configuration-only (FR-41).
            var harness = new SettingsHarness();

            var replies = await ExecuteSettings(harness, "!settings cooldown 4 5", invokerTier: PermissionTier.BotOwner);

            StringAssert.Contains(replies[0], "tiers 0 to 3");
            Assert.AreEqual(0, harness.Database.GuildSettings.Count(), "An out-of-scale tier must not write anything");
        }

        [TestMethod]
        public async Task Settings_CooldownNegativeSeconds_IsRejected()
        {
            var harness = new SettingsHarness();

            var replies = await ExecuteSettings(harness, "!settings cooldown 0 -5", invokerTier: PermissionTier.Admin);

            StringAssert.Contains(replies[0], "0 or more");
            Assert.AreEqual(0, harness.Database.GuildSettings.Count(), "A negative window must not write anything");
        }

        [TestMethod]
        public async Task Settings_CooldownEscalationGuard_RejectsOwnAndHigherTiers()
        {
            // Mirrors FR-36: an invoker may only configure tiers strictly below their own —
            // nobody can loosen the throttle that governs their own level.
            var harness = new SettingsHarness();

            var replies = await ExecuteSettings(harness, "!settings cooldown 2 5", invokerTier: PermissionTier.Admin);

            StringAssert.Contains(replies[0], "below your own");
            Assert.AreEqual(0, harness.Database.GuildSettings.Count(), "A guard rejection must not write anything");
        }

        [TestMethod]
        public async Task Settings_CooldownBotOwner_CanConfigureAnyTier()
        {
            // The bot owner (tier 4) is never bound by a guild's settings, so they may configure
            // every tier — including the owner tier that no guild-level admin can reach.
            var harness = new SettingsHarness();

            var replies = await ExecuteSettings(harness, "!settings cooldown 3 5", botOwnerId: 200);

            StringAssert.Contains(replies[0], "Cooldown for tier 3 in this server set to 5 seconds");
            Assert.AreEqual(5, harness.Database.GuildSettings.Single(setting => setting.GuildId == GuildId).CooldownSecondsTier3);
        }

        [TestMethod]
        public async Task Settings_Overview_Shows_CooldownOverrides()
        {
            var harness = new SettingsHarness();
            harness.Database.GuildSettings.Add(new DatabaseContext.GuildSetting { GuildId = GuildId, CooldownSecondsTier0 = 5 });
            harness.Database.SaveChanges();

            var replies = await ExecuteSettings(harness, "!settings");

            StringAssert.Contains(replies[1], "tier 0: 5 seconds");
        }

        [TestMethod]
        public async Task Settings_Overview_Shows_NoCooldownOverrides_When_None_Set()
        {
            var harness = new SettingsHarness();

            var replies = await ExecuteSettings(harness, "!settings");

            StringAssert.Contains(replies[1], "Cooldown overrides: none set");
        }
    }
}
