using Discord;
using HambotV2.Commands;
using HambotV2.Connections;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BotTests
{
    /// <summary>
    /// Command cooldowns (NFR-12): the per-(guild, user, command) in-memory throttle,
    /// per-tier window resolution (global map, guild override, reset fallback, 0 disables), the
    /// tier-4 and DM scope exclusions, the pipeline throttle reply, and the !settings cooldown
    /// subcommand with its escalation guard.
    /// </summary>
    [TestClass]
    public sealed class TestCooldowns
    {
        private const ulong GuildId = 5;
        private const ulong UserId = 100;

        /// <summary>
        /// A real CooldownService over an isolated in-memory store and a fake clock, so window expiry
        /// is tested by advancing time instead of sleeping. Fresh contexts over the same store mimic
        /// the transient per-message contexts in production (each handler disposes its own).
        /// </summary>
        private sealed class CooldownHarness
        {
            private readonly string _storeName = Guid.NewGuid().ToString();

            public DatabaseContext Database { get; }
            public FakeTimeProvider Time { get; } = new(DateTimeOffset.UnixEpoch);
            public CooldownService Service { get; }

            public CooldownHarness(IReadOnlyDictionary<int, int>? globalCooldownsByTier = null)
            {
                Database = CreateContext();

                var values = new Dictionary<string, string?> { { "BotConfiguration:CommandIdentifier", "!" } };
                if (globalCooldownsByTier != null)
                {
                    foreach (var (tier, seconds) in globalCooldownsByTier)
                        values[$"BotConfiguration:CooldownSecondsByTier:{tier}"] = seconds.ToString();
                }

                var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
                Service = new CooldownService(Time, configuration);
            }

            public DatabaseContext CreateContext()
                => new(new DbContextOptionsBuilder<DatabaseContext>().UseInMemoryDatabase(_storeName).Options);

            public CooldownCheck Invoke(ulong? guildId = GuildId, ulong userId = UserId, string commandName = "hello", int userTier = PermissionTier.Everyone)
                => Service.TryBeginInvocation(guildId, userId, commandName, userTier, Database);
        }

        [TestMethod]
        public void SecondInvocationInsideWindow_IsThrottled_WithRemainingWait()
        {
            var harness = new CooldownHarness();

            Assert.IsTrue(harness.Invoke().IsAllowed, "The first invocation must pass");

            CooldownCheck second = harness.Invoke();

            Assert.IsFalse(second.IsAllowed, "The second invocation inside the default 3-second window must be throttled");
            Assert.IsTrue(second.RemainingWait > TimeSpan.Zero && second.RemainingWait <= TimeSpan.FromSeconds(3), "The remaining wait must name the window left");
        }

        [TestMethod]
        public void InvocationAfterWindowExpires_IsAllowedAgain()
        {
            var harness = new CooldownHarness();
            harness.Invoke();

            harness.Time.Advance(TimeSpan.FromSeconds(3));

            Assert.IsTrue(harness.Invoke().IsAllowed, "After the window expires the invocation must be allowed again");
        }

        [TestMethod]
        public void Throttles_AreIndependent_PerUser_PerCommand_And_PerGuild()
        {
            var harness = new CooldownHarness();
            harness.Invoke();

            Assert.IsFalse(harness.Invoke().IsAllowed, "Sanity: same user, command and guild is throttled");
            Assert.IsTrue(harness.Invoke(userId: 200).IsAllowed, "Another user has their own window");
            Assert.IsTrue(harness.Invoke(commandName: "help").IsAllowed, "Another command has its own window");
            Assert.IsTrue(harness.Invoke(guildId: 77).IsAllowed, "Another guild has its own window");
        }

        [TestMethod]
        public void GlobalMap_GivesEachTierItsConfiguredWindow()
        {
            var harness = new CooldownHarness(new Dictionary<int, int> { { PermissionTier.Everyone, 10 }, { PermissionTier.Moderator, 1 } });

            Assert.IsTrue(harness.Invoke(userTier: PermissionTier.Everyone).IsAllowed);
            harness.Time.Advance(TimeSpan.FromSeconds(2));
            Assert.IsFalse(harness.Invoke(userTier: PermissionTier.Everyone).IsAllowed, "Tier 0 must still be inside its 10-second window");

            Assert.IsTrue(harness.Invoke(userTier: PermissionTier.Moderator).IsAllowed);
            harness.Time.Advance(TimeSpan.FromSeconds(2));
            Assert.IsTrue(harness.Invoke(userTier: PermissionTier.Moderator).IsAllowed, "Tier 1 must have passed its 1-second window");
        }

        [TestMethod]
        public void MissingMapEntries_DefaultToThreeSeconds()
        {
            var harness = new CooldownHarness();
            harness.Invoke(userTier: PermissionTier.Admin);

            harness.Time.Advance(TimeSpan.FromSeconds(2));
            Assert.IsFalse(harness.Invoke(userTier: PermissionTier.Admin).IsAllowed, "A tier without a map entry uses the 3-second default");

            harness.Time.Advance(TimeSpan.FromSeconds(1));
            Assert.IsTrue(harness.Invoke(userTier: PermissionTier.Admin).IsAllowed, "Exactly at the default window the invocation is allowed again");
        }

        [TestMethod]
        public void ZeroWindow_DisablesThrottlingForThatTier()
        {
            var harness = new CooldownHarness(new Dictionary<int, int> { { PermissionTier.Everyone, 0 } });

            Assert.IsTrue(harness.Invoke().IsAllowed);
            Assert.IsTrue(harness.Invoke().IsAllowed, "A window of 0 must disable throttling for that tier");
        }

        [TestMethod]
        public void GuildOverride_BeatsTheGlobalDefault_AndResetFallsBack()
        {
            var harness = new CooldownHarness(new Dictionary<int, int> { { PermissionTier.Everyone, 10 } });
            var setting = new DatabaseContext.GuildSetting { GuildId = GuildId, CooldownSecondsTier0 = 2 };
            harness.Database.GuildSettings.Add(setting);
            harness.Database.SaveChanges();

            Assert.IsTrue(harness.Invoke().IsAllowed);
            harness.Time.Advance(TimeSpan.FromSeconds(3));
            Assert.IsTrue(harness.Invoke().IsAllowed, "The guild's 2-second override must beat the global 10-second default");

            setting.SetCooldownOverride(PermissionTier.Everyone, null);  // what !settings cooldown reset persists
            harness.Database.SaveChanges();

            // The reset falls back to the global 10-second default: 3 seconds after the last
            // invocation the user is still inside the window, and only after 10 seconds is free.
            Assert.IsFalse(harness.Invoke().IsAllowed, "After the reset the global 10-second default applies again");
            harness.Time.Advance(TimeSpan.FromSeconds(10));
            Assert.IsTrue(harness.Invoke().IsAllowed);
        }

        [TestMethod]
        public void GuildOverrides_OnlyAffectTheirOwnGuild()
        {
            var harness = new CooldownHarness(new Dictionary<int, int> { { PermissionTier.Everyone, 10 } });
            harness.Database.GuildSettings.Add(new DatabaseContext.GuildSetting { GuildId = GuildId, CooldownSecondsTier0 = 0 });
            harness.Database.SaveChanges();

            Assert.IsTrue(harness.Invoke().IsAllowed);
            Assert.IsTrue(harness.Invoke().IsAllowed, "The overriding guild must not throttle tier 0");

            Assert.IsTrue(harness.Invoke(guildId: 77).IsAllowed);
            Assert.IsFalse(harness.Invoke(guildId: 77).IsAllowed, "Other guilds keep the global default");
        }

        [TestMethod]
        public void BotOwnerTier_IsNeverBoundByAGuildOverride()
        {
            var harness = new CooldownHarness(new Dictionary<int, int> { { PermissionTier.BotOwner, 3 } });
            // A guild trying to disable every configurable tier must not reach tier 4 (FR-41).
            harness.Database.GuildSettings.Add(new DatabaseContext.GuildSetting
            {
                GuildId = GuildId,
                CooldownSecondsTier0 = 0,
                CooldownSecondsTier1 = 0,
                CooldownSecondsTier2 = 0,
                CooldownSecondsTier3 = 0
            });
            harness.Database.SaveChanges();

            Assert.IsTrue(harness.Invoke(userTier: PermissionTier.BotOwner).IsAllowed);
            Assert.IsFalse(harness.Invoke(userTier: PermissionTier.BotOwner).IsAllowed, "Tier 4 always uses the global default, never a guild override");
        }

        [TestMethod]
        public void DirectMessages_UseTheSentinelGuildKey_AndTheTierThreeWindow()
        {
            var harness = new CooldownHarness(new Dictionary<int, int> { { PermissionTier.Owner, 5 } });

            // DM scope: the user acts at owner tier (FR-25) and no guild state is read.
            Assert.IsTrue(harness.Invoke(guildId: null, userTier: PermissionTier.Owner).IsAllowed);
            Assert.IsFalse(harness.Invoke(guildId: null, userTier: PermissionTier.Owner).IsAllowed, "Repeated DM invocations must be throttled through the tier-3 window");

            Assert.IsTrue(harness.Invoke(guildId: null, userTier: PermissionTier.Owner, commandName: "help").IsAllowed);

            // The sentinel key never collides with a real guild's throttle.
            harness.Invoke(guildId: GuildId);
            Assert.IsFalse(harness.Invoke(guildId: GuildId).IsAllowed, "Sanity: the guild invocation is throttled");
            Assert.IsTrue(harness.Invoke(guildId: null, userTier: PermissionTier.Owner, commandName: "another").IsAllowed);
        }

        private static IGuildSettingsService StubGuildSettings()
        {
            var guildSettings = new Mock<IGuildSettingsService>();
            guildSettings.Setup(service => service.GetCommandIdentifier(It.IsAny<ulong>())).Returns("!");
            guildSettings.SetupGet(service => service.GlobalCommandIdentifier).Returns("!");
            return guildSettings.Object;
        }

        [TestMethod]
        public async Task HandleCommand_Throttled_RepliesWithRemainingWait_AndSkipsCommand()
        {
            var harness = new CooldownHarness(new Dictionary<int, int> { { PermissionTier.Everyone, 30 } });
            var command = DiscordTestDoubles.CreateMockCommand("greet");
            var commandService = new CommandService();
            commandService.RegisterCommand("greet", command.Object);

            var sentMessages = new List<string>();
            var message = DiscordTestDoubles.CreateGuildMessage("!greet", UserId, 999, sentMessages);

            var permissions = new Mock<IPermissionsService>();
            permissions
                .Setup(p => p.CheckUserPermissions(It.IsAny<ulong>(), It.IsAny<ulong?>(), It.IsAny<TierOverrides>(), It.IsAny<ICommand>(), It.IsAny<IDatabaseContext>()))
                .ReturnsAsync(new PermissionCheckResult(IsAllowed: true, UserEffectiveTier: PermissionTier.Everyone));

            CommandHandler CreateHandler() => new CommandHandler(
                commandService,
                StubGuildSettings(),
                permissions.Object,
                harness.Service,
                harness.CreateContext(),
                Mock.Of<IConfiguration>(),
                Mock.Of<ILogger<CommandHandler>>());

            await CreateHandler().HandleCommand(message.Object);   // first invocation passes the throttle
            await CreateHandler().HandleCommand(message.Object);  // second is inside the 30-second window

            command.Verify(c => c.ExecuteCommand(It.IsAny<IMessage>(), It.IsAny<ICommandContext>()), Times.Once, "A throttled invocation must not execute the command");
            Assert.AreEqual(1, sentMessages.Count, "Only the throttle reply may be sent");
            StringAssert.Contains(sentMessages[0], "too quickly");
            StringAssert.Contains(sentMessages[0], "more seconds");
        }

        [TestMethod]
        public async Task HandleCommand_Throttled_LogsThrottledAuditEvent()
        {
            var harness = new CooldownHarness();
            var command = DiscordTestDoubles.CreateMockCommand("greet");
            var commandService = new CommandService();
            commandService.RegisterCommand("greet", command.Object);

            var sentMessages = new List<string>();
            var message = DiscordTestDoubles.CreateGuildMessage("!greet", UserId, 999, sentMessages);

            var permissions = new Mock<IPermissionsService>();
            permissions
                .Setup(p => p.CheckUserPermissions(It.IsAny<ulong>(), It.IsAny<ulong?>(), It.IsAny<TierOverrides>(), It.IsAny<ICommand>(), It.IsAny<IDatabaseContext>()))
                .ReturnsAsync(new PermissionCheckResult(IsAllowed: true, UserEffectiveTier: PermissionTier.Everyone));

            var logger = new Mock<ILogger<CommandHandler>>();
            CommandHandler CreateHandler() => new CommandHandler(
                commandService, StubGuildSettings(), permissions.Object, harness.Service, harness.CreateContext(), Mock.Of<IConfiguration>(), logger.Object);

            await CreateHandler().HandleCommand(message.Object);
            await CreateHandler().HandleCommand(message.Object);

            logger.Verify(l => l.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state != null && state.ToString()!.Contains("outcome Throttled", StringComparison.Ordinal)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        }
    }
}
