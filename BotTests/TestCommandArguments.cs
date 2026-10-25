using HambotV2.Commands;

namespace BotTests
{
    /// <summary>
    /// The shared argument-parsing helper (NFR-13): tokenization of command messages
    /// and typed accessors (string, int, Discord id/mention).
    /// </summary>
    [TestClass]
    public sealed class TestCommandArguments
    {
        [TestMethod]
        public void Parse_StripsIdentifierAndCommandToken()
        {
            var arguments = CommandArguments.Parse("!userpermission <@500> 1");

            Assert.AreEqual(2, arguments.Count);
            Assert.AreEqual("<@500>", arguments.Get(0));
            Assert.AreEqual("1", arguments.Get(1));
        }

        [TestMethod]
        public void Parse_IdentifierAndName_SingleToken()
        {
            // No space between identifier and command name: the token still holds both.
            var arguments = CommandArguments.Parse("!help");

            Assert.AreEqual(0, arguments.Count);
            Assert.IsNull(arguments.Get(0));
        }

        [TestMethod]
        public void Parse_CollapsesRepeatedSpaces()
        {
            var arguments = CommandArguments.Parse("!hello   world     again");

            Assert.AreEqual(2, arguments.Count);
            Assert.AreEqual("world", arguments.Get(0));
            Assert.AreEqual("again", arguments.Get(1));
        }

        [TestMethod]
        public void Get_OutOfRange_ReturnsNull()
        {
            var arguments = CommandArguments.Parse("!hello world");

            Assert.IsNull(arguments.Get(1));
            Assert.IsNull(arguments.Get(-1));
        }

        [TestMethod]
        public void TryGetString_PresentAndAbsent()
        {
            var arguments = CommandArguments.Parse("!commandpermission hello");

            Assert.IsTrue(arguments.TryGetString(0, out string commandName));
            Assert.AreEqual("hello", commandName);

            Assert.IsFalse(arguments.TryGetString(1, out string absent));
            Assert.AreEqual(string.Empty, absent);
        }

        [TestMethod]
        public void TryGetInt_ParsesAndRejects()
        {
            var arguments = CommandArguments.Parse("!commandpermission hello 2");

            Assert.IsTrue(arguments.TryGetInt(1, out int tier));
            Assert.AreEqual(2, tier);

            Assert.IsFalse(arguments.TryGetInt(0, out _), "A command name is not an int");
            Assert.IsFalse(arguments.TryGetInt(2, out _), "Absent argument is not an int");
        }

        [TestMethod]
        public void TryGetDiscordUserId_AcceptsRawIdAndMentions()
        {
            var raw = CommandArguments.Parse("!userpermission 500 1");
            Assert.IsTrue(raw.TryGetDiscordUserId(0, out ulong rawId));
            Assert.AreEqual(500UL, rawId);

            var mention = CommandArguments.Parse("!userpermission <@500>");
            Assert.IsTrue(mention.TryGetDiscordUserId(0, out ulong mentionId));
            Assert.AreEqual(500UL, mentionId);

            var legacyMention = CommandArguments.Parse("!userpermission <@!500>");
            Assert.IsTrue(legacyMention.TryGetDiscordUserId(0, out ulong legacyId));
            Assert.AreEqual(500UL, legacyId);
        }

        [TestMethod]
        public void TryGetDiscordUserId_RejectsGarbageAndAbsentArgument()
        {
            var garbage = CommandArguments.Parse("!userpermission notauser 1");
            Assert.IsFalse(garbage.TryGetDiscordUserId(0, out _));

            var absent = CommandArguments.Parse("!userpermission");
            Assert.IsFalse(absent.TryGetDiscordUserId(0, out _));
        }
    }
}
