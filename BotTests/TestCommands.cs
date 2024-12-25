namespace BotTests
{
    [TestClass]
    public sealed class TestCommands
    {
        [TestMethod]
        public void Get_Helloworld_Output()
        {
            var command = new HambotV2.Commands.SystemCommands.HelloWorld();

            Assert.IsTrue(command.GetResponse() == "Hello World");
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
