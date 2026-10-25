using HambotV2;
using HambotV2.Commands;
using HambotV2.Commands.SystemCommands;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.RegularExpressions;

namespace BotTests
{
    /// <summary>
    /// The version feature (FR-76–FR-77, NFR-17): the pure tag→version formatting with its
    /// fallbacks, the embedded version the provider reads, and the `!version` command —
    /// registration (lowercase name, help text, default tier 0) and its reply, including in
    /// direct messages.
    /// </summary>
    [TestClass]
    public sealed class TestVersion
    {
        // --- Pure formatting (NFR-17) ---

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
            // FR-76: no reachable tag -> 0.0.0+<hash>, never a wrong release number.
            Assert.AreEqual(expected, BotVersion.Format(tag, commitHash));
        }

        [TestMethod]
        [DataRow("", "")]
        [DataRow(null, null)]
        [DataRow("   ", "  ")]
        public void Format_No_Git_Metadata_Reports_The_Unknown_Fallback(string? tag, string? commitHash)
        {
            // FR-76: builds without git metadata report 0.0.0+unknown.
            Assert.AreEqual("0.0.0+unknown", BotVersion.Format(tag, commitHash));
        }

        [TestMethod]
        [DataRow(" v1.2.3\r", " 1639324\r\n", "1.2.3+1639324")]
        [DataRow("\tv1.2.3 ", "\t1639324 ", "1.2.3+1639324")]
        public void Format_Trims_MSBuild_Exec_Line_Endings(string tag, string commitHash, string expected)
        {
            // MSBuild's ConsoleToMSBuild capture can carry trailing CR/LF characters; the
            // container build passes the values through environment strings of its own.
            Assert.AreEqual(expected, BotVersion.Format(tag, commitHash));
        }

        [TestMethod]
        public void Format_Bare_V_Prefix_Does_Not_Become_An_Empty_Semver()
        {
            Assert.AreEqual("0.0.0+1639324", BotVersion.Format("v", "1639324"));
        }

        // --- The embedded version the provider reads (FR-76) ---

        [TestMethod]
        public void Current_Reports_The_Build_Embedded_Version_In_Semver_Metadata_Shape()
        {
            // The test build compiles HambotV2 with the MSBuild target, so the metadata is
            // real: a machine without git reports the 0.0.0+unknown fallback; a tagged or
            // untagged checkout reports its tag semver plus the commit hash. The exact value
            // depends on the checkout, so pin the shape — never a wrong release number shape.
            Assert.IsTrue(
                Regex.IsMatch(BotVersion.Current, @"^\d+\.\d+\.\d+\+\S+$"),
                $"'{BotVersion.Current}' must look like <semver>+<commit>");
        }

        // --- !version command: registration (FR-77) ---

        [TestMethod]
        public void Version_Command_Name_Is_Lowercase()
        {
            var command = new VersionCommand();

            Assert.AreEqual("version", command.Name);
            Assert.IsTrue(command.Name == command.Name.ToLower());
        }

        [TestMethod]
        public void Version_Command_Provides_Help_Text_And_Defaults_To_Tier_0()
        {
            var command = new VersionCommand();

            Assert.IsFalse(string.IsNullOrWhiteSpace(command.Description), "Description is required (FR-39)");
            Assert.IsFalse(string.IsNullOrWhiteSpace(command.Usage), "Usage is required (FR-39)");
            Assert.AreEqual(0, command.DefaultPermissionTier, "version must be available to everyone");
        }

        [TestMethod]
        public void Version_Command_Is_Registered()
        {
            var commandService = new CommandService();

            commandService.RegisterCommands();

            Assert.IsTrue(commandService.TryGetCommand("!version", "!", out ICommand? command), "the command pipeline must resolve !version");
            Assert.IsInstanceOfType(command, typeof(VersionCommand));
        }

        // --- !version command: the reply (FR-77) ---

        [TestMethod]
        public async Task Version_Command_Replies_With_The_Current_Version()
        {
            var command = new VersionCommand();
            var sentMessages = new List<string>();
            var message = DiscordTestDoubles.CreateGuildMessage(
                "!version", 200, 999, sentMessages, DiscordTestDoubles.DefaultGuildId, DateTimeOffset.UnixEpoch);
            var context = DiscordTestDoubles.CreateContext(DiscordTestDoubles.CreateDatabase());

            await command.ExecuteCommand(message.Object, context);

            CollectionAssert.AreEqual(new[] { BotVersion.Current }, sentMessages,
                "the reply is exactly the version string, the same value the startup log line shows");
        }

        [TestMethod]
        public async Task Version_Command_Works_In_Direct_Messages()
        {
            var command = new VersionCommand();
            var sentMessages = new List<string>();
            var message = DiscordTestDoubles.CreateDirectMessage("!version", 200, sentMessages, DateTimeOffset.UnixEpoch);
            var context = DiscordTestDoubles.CreateContext(DiscordTestDoubles.CreateDatabase(), guildId: null);

            await command.ExecuteCommand(message.Object, context);

            CollectionAssert.AreEqual(new[] { BotVersion.Current }, sentMessages,
                "version reads no guild state, so it answers in DMs too (FR-25)");
        }
    }
}
