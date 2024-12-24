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
    }
}
