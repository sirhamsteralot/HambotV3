using HambotV2.Commands;
using HambotV2.Commands.SystemCommands;

namespace BotTests
{
    [TestClass]
    public sealed class TestCommandInfrastructure
    {
        private static ICommandService CreateService()
        {
            return new CommandService();
        }

        [TestMethod]
        public void CommandService_Register_All_Commands()
        {
            ICommandService commandService = CreateService();

            commandService.RegisterCommands();

            Assert.IsTrue(commandService.CommandsRegistered);
            Assert.IsTrue(commandService.Commands.Count > 0);
        }

        [TestMethod]
        public void CommandService_All_Registered_Commands_Satisfy_Contract()
        {
            ICommandService commandService = CreateService();

            commandService.RegisterCommands();

            foreach (ICommand command in commandService.Commands.Values)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(command.Description), $"{command.Name} must declare a Description");
                Assert.IsFalse(string.IsNullOrWhiteSpace(command.Usage), $"{command.Name} must declare Usage");
                Assert.IsTrue(PermissionTier.IsValid(command.DefaultPermissionTier), $"{command.Name} must declare a valid default permission tier");
                Assert.AreEqual(command.Name.ToLowerInvariant(), command.Name, $"{command.Name} must be lowercase");
            }
        }

        [TestMethod]
        public void CommandService_Get_Helloworld_Command_Success()
        {
            ICommandService commandService = CreateService();

            commandService.RegisterCommands();

            Assert.IsTrue(commandService.TryGetCommand("!hello", "!", out ICommand? command));
            Assert.IsNotNull(command);
        }

        [TestMethod]
        public void CommandService_Unknown_And_NonCommand_Content_Is_Not_A_Command()
        {
            ICommandService commandService = CreateService();

            commandService.RegisterCommands();

            Assert.IsFalse(commandService.TryGetCommand("!gibberishcommandlaksjf;lsakjdf;laskjdf;alk asdfa", "!", out ICommand? command));
            Assert.IsNull(command);
            Assert.IsFalse(commandService.TryGetCommand("hello", "!", out _));
            Assert.IsFalse(commandService.TryGetCommand("", "!", out _));
        }

        [TestMethod]
        [DataRow("!")]
        [DataRow("!   ")]
        [DataRow(" ! ")]
        [DataRow("")]
        public void CommandService_Identifier_Only_Message_Is_Not_A_Command(string content)
        {
            ICommandService commandService = CreateService();
            commandService.RegisterCommands();

            Assert.IsFalse(commandService.TryGetCommand(content, "!", out ICommand? command));
            Assert.IsNull(command);
        }

        [TestMethod]
        public void CommandService_IsIdentifierOnlyMessage()
        {
            ICommandService commandService = CreateService();
            commandService.RegisterCommands();

            Assert.IsTrue(commandService.IsIdentifierOnlyMessage("!", "!"));
            Assert.IsTrue(commandService.IsIdentifierOnlyMessage("  !   ", "!"));
            Assert.IsFalse(commandService.IsIdentifierOnlyMessage("!hello", "!"));
            Assert.IsFalse(commandService.IsIdentifierOnlyMessage("hello", "!"));
            Assert.IsFalse(commandService.IsIdentifierOnlyMessage("", "!"));
        }

        [TestMethod]
        [DataRow("!hello")]
        [DataRow("!hello world")]
        [DataRow("   !hello   ")]
        [DataRow("!   hello")]
        [DataRow("!HELLO")]
        public void CommandService_Parses_Command_Regardless_Of_Whitespace_And_Case(string content)
        {
            ICommandService commandService = CreateService();
            commandService.RegisterCommands();

            Assert.IsTrue(commandService.TryGetCommand(content, "!", out ICommand? command), $"'{content}' must resolve to the hello command");
            Assert.AreEqual("hello", command.Name);
        }

        [TestMethod]
        public void CommandService_Supports_Custom_Identifier()
        {
            ICommandService commandService = CreateService();

            commandService.RegisterCommands();

            Assert.IsTrue(commandService.TryGetCommand("//hello", "//", out ICommand? command));
            Assert.AreEqual("hello", command.Name);
            Assert.IsFalse(commandService.TryGetCommand("!hello", "//", out _));
            Assert.IsTrue(commandService.IsIdentifierOnlyMessage("//", "//"));
        }

        [TestMethod]
        public void CommandService_Register_Uppercase_Name_Is_Rejected()
        {
            ICommandService commandService = CreateService();

            Assert.ThrowsExactly<ArgumentException>(() =>
                commandService.RegisterCommand("Hello", DiscordTestDoubles.CreateMockCommand("Hello").Object));
        }

        [TestMethod]
        public void CommandService_Register_Command_Without_Help_Text_Is_Rejected()
        {
            ICommandService commandService = CreateService();

            var withoutDescription = DiscordTestDoubles.CreateMockCommand("nodesc");
            withoutDescription.SetupGet(c => c.Description).Returns(string.Empty);

            Assert.ThrowsExactly<ArgumentException>(() => commandService.RegisterCommand("nodesc", withoutDescription.Object));

            var withoutUsage = DiscordTestDoubles.CreateMockCommand("nousage");
            withoutUsage.SetupGet(c => c.Usage).Returns(" ");

            Assert.ThrowsExactly<ArgumentException>(() => commandService.RegisterCommand("nousage", withoutUsage.Object));
        }

        [TestMethod]
        public void CommandService_Register_Command_With_Invalid_Default_Tier_Is_Rejected()
        {
            ICommandService commandService = CreateService();

            Assert.ThrowsExactly<ArgumentException>(() =>
                commandService.RegisterCommand("badtier", DiscordTestDoubles.CreateMockCommand("badtier", defaultPermissionTier: 5).Object));
        }
    }
}
