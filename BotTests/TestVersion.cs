using HambotV2;
using HambotV2.Commands;
using HambotV2.Commands.SystemCommands;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.RegularExpressions;

namespace BotTests
{
    [TestClass]
    public sealed class TestVersion
    {
        [TestMethod]
        [DataRow("v1.2.3", "1639324", "1.2.3+1639324")]
        [DataRow("v0.0.1", "abc1234", "0.0.1+abc1234")]
        [DataRow("v10.20.30", "ca6ba60", "10.20.30+ca6ba60")]
        public void Format_Joins_Tag_Semver_And_Commit_Hash(string tag, string commitHash, string expected)
        {
            Assert.AreEqual(expected, BotVersion.Format(tag, commitHash));
        }

        [TestMethod]
        [DataRow("", "1639324", "0.0.0+1639324")]
        [DataRow(null, "1639324", "0.0.0+1639324")]
        public void Format_Untagged_Build_Reports_Fallback_Semver_Plus_Hash(string? tag, string commitHash, string expected)
        {
            Assert.AreEqual(expected, BotVersion.Format(tag, commitHash));
        }

        [TestMethod]
        [DataRow("", "")]
        [DataRow(null, null)]
        [DataRow("   ", "  ")]
        public void Format_No_Git_Metadata_Reports_The_Unknown_Fallback(string? tag, string? commitHash)
        {
            Assert.AreEqual("0.0.0+unknown", BotVersion.Format(tag, commitHash));
        }

        [TestMethod]
        [DataRow(" v1.2.3\r", " 1639324\r\n", "1.2.3+1639324")]
        [DataRow("\tv1.2.3 ", "\t1639324 ", "1.2.3+1639324")]
        public void Format_Trims_MSBuild_Exec_Line_Endings(string tag, string commitHash, string expected)
        {
            // MSBuild's ConsoleToMSBuild capture can carry trailing CR/LF characters.
            Assert.AreEqual(expected, BotVersion.Format(tag, commitHash));
        }

        [TestMethod]
        public void Format_Bare_V_Prefix_Does_Not_Become_An_Empty_Semver()
        {
            Assert.AreEqual("0.0.0+1639324", BotVersion.Format("v", "1639324"));
        }

        [TestMethod]
        public void Current_Reports_The_Build_Embedded_Version_In_Semver_Metadata_Shape()
        {
            // The exact value depends on the checkout, so pin the shape, never a version.
            Assert.IsTrue(
                Regex.IsMatch(BotVersion.Current, @"^\d+\.\d+\.\d+\+\S+$"),
                $"'{BotVersion.Current}' must look like <semver>+<commit>");
        }

        [TestMethod]
        public void Version_Command_Is_Registered_And_Available_To_Everyone()
        {
            var commandService = new CommandService();

            commandService.RegisterCommands();

            Assert.IsTrue(commandService.TryGetCommand("!version", "!", out ICommand? command));
            Assert.IsInstanceOfType(command, typeof(VersionCommand));
            Assert.AreEqual(0, command.DefaultPermissionTier);
        }

        [TestMethod]
        public async Task Version_Command_Replies_With_The_Current_Version()
        {
            var command = new VersionCommand();
            var sentMessages = new List<string>();
            var message = DiscordTestDoubles.CreateGuildMessage(
                "!version", 200, 999, sentMessages, DiscordTestDoubles.DefaultGuildId, DateTimeOffset.UnixEpoch);
            var context = DiscordTestDoubles.CreateContext(DiscordTestDoubles.CreateDatabase());

            await command.ExecuteCommand(message.Object, context);

            CollectionAssert.AreEqual(new[] { BotVersion.Current }, sentMessages);
        }

        [TestMethod]
        public async Task Version_Command_Works_In_Direct_Messages()
        {
            var command = new VersionCommand();
            var sentMessages = new List<string>();
            var message = DiscordTestDoubles.CreateDirectMessage("!version", 200, sentMessages, DateTimeOffset.UnixEpoch);
            var context = DiscordTestDoubles.CreateContext(DiscordTestDoubles.CreateDatabase(), guildId: null);

            await command.ExecuteCommand(message.Object, context);

            CollectionAssert.AreEqual(new[] { BotVersion.Current }, sentMessages);
        }
    }
}
