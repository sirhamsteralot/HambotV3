namespace BotTests
{
    [TestClass]
    public sealed class TestCommands
    {
        [TestMethod]
        public async Task HelloWorld_Replies_With_The_Greeting()
        {
            var command = new HambotV2.Commands.SystemCommands.HelloWorld();
            var sentMessages = new List<string>();
            var message = DiscordTestDoubles.CreateGuildMessage("!hello", 1, 999, sentMessages);

            await command.ExecuteCommand(message.Object, DiscordTestDoubles.CreateContext(DiscordTestDoubles.CreateDatabase()));

            CollectionAssert.AreEqual(new[] { "Hello World" }, sentMessages);
        }

        [TestMethod]
        public void Verify_Lowercase_Name_HelloWorld()
        {
            var command = new HambotV2.Commands.SystemCommands.HelloWorld();

            Assert.IsTrue(command.Name == command.Name.ToLower());
        }

        [TestMethod]
        public void Verify_Lowercase_Name_CommandPermission()
        {
            var command = new HambotV2.Commands.SystemCommands.CommandPermission();

            Assert.IsTrue(command.Name == command.Name.ToLower());
        }

        [TestMethod]
        public void Verify_Lowercase_Name_UserPermission()
        {
            var command = new HambotV2.Commands.SystemCommands.UserPermission();

            Assert.IsTrue(command.Name == command.Name.ToLower());
        }
    }
}
