using HambotV2.Commands;
using HambotV2.Commands.SystemCommands;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BotTests
{
    [TestClass]
    public sealed class TestCommandInfrastructure
    {
        private static ICommandService CreateService(string identifier = "!")
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> {
                    {"BotConfiguration:CommandIdentifier", identifier},
                })
                .Build();

            return new CommandService(Mock.Of<ILogger<CommandService>>(), configuration);
        }

        [TestMethod]
        public void CommandService_Register_Command_HelloWorld()
        {
            ICommandService commandService = CreateService();

            Assert.IsTrue(commandService.Commands.Count == 0);

            commandService.RegisterCommand("hello", new HelloWorld());

            Assert.IsTrue(commandService.Commands.Count > 0);
        }

        [TestMethod]
        public void CommandService_Register_All_Commands()
        {
            ICommandService commandService = CreateService();

            Assert.IsTrue(commandService.Commands.Count == 0);

            commandService.RegisterCommands();

            Assert.IsTrue(commandService.CommandsRegistered);
            Assert.IsTrue(commandService.Commands.Count > 0);
        }

        [TestMethod]
        public void CommandService_Register_System_Commands()
        {
            ICommandService commandService = CreateService();

            Assert.IsTrue(commandService.Commands.Count == 0);

            RegisterSystemCommands.RegisterCommands(commandService);

            Assert.IsTrue(commandService.Commands.Count > 0);
        }

        [TestMethod]
        public void CommandService_Get_Helloworld_Command_Success()
        {
            ICommandService commandService = CreateService();

            commandService.RegisterCommands();

            bool returnValue = commandService.TryGetCommand("!hello", out ICommand? command);
            Assert.IsTrue(returnValue);
            Assert.IsNotNull(command);
        }

        [TestMethod]
        public void CommandService_Get_Invalid_Command()
        {
            ICommandService commandService = CreateService();

            commandService.RegisterCommands();

            bool returnValue = commandService.TryGetCommand("!gibberishcommandlaksjf;lsakjdf;laskjdf;alk asdfa", out ICommand? command);
            Assert.IsFalse(returnValue);
            Assert.IsNull(command);
        }

        [TestMethod]
        public void CommandService_All_Registered_Commands_Satisfy_Contract()
        {
            // Roadmap T17 (FR-39): every command must carry help text, a valid default tier and a
            // lowercase name; registration itself rejects anything else.
            ICommandService commandService = CreateService();

            commandService.RegisterCommands();

            Assert.IsTrue(commandService.Commands.Count > 0);
            foreach (ICommand command in commandService.Commands.Values)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(command.Description), $"{command.Name} must declare a Description");
                Assert.IsFalse(string.IsNullOrWhiteSpace(command.Usage), $"{command.Name} must declare Usage");
                Assert.IsTrue(PermissionTier.IsValid(command.DefaultPermissionTier), $"{command.Name} must declare a valid default permission tier");
                Assert.AreEqual(command.Name.ToLowerInvariant(), command.Name, $"{command.Name} must be lowercase");
            }
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

            Assert.IsFalse(commandService.TryGetCommand(content, out ICommand? command));
            Assert.IsNull(command);
        }

        [TestMethod]
        public void CommandService_IsIdentifierOnlyMessage_True_For_Bare_Identifier()
        {
            ICommandService commandService = CreateService();
            commandService.RegisterCommands();

            Assert.IsTrue(commandService.IsIdentifierOnlyMessage("!"));
            Assert.IsTrue(commandService.IsIdentifierOnlyMessage("  !   "));
        }

        [TestMethod]
        public void CommandService_IsIdentifierOnlyMessage_False_For_Named_Commands()
        {
            ICommandService commandService = CreateService();
            commandService.RegisterCommands();

            Assert.IsFalse(commandService.IsIdentifierOnlyMessage("!hello"));
            Assert.IsFalse(commandService.IsIdentifierOnlyMessage("hello"));
            Assert.IsFalse(commandService.IsIdentifierOnlyMessage(""));
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

            Assert.IsTrue(commandService.TryGetCommand(content, out ICommand? command), $"'{content}' must resolve to the hello command");
            Assert.AreEqual("hello", command.Name);
        }

        [TestMethod]
        public void CommandService_NonCommandContent_Is_Not_A_Command()
        {
            ICommandService commandService = CreateService();
            commandService.RegisterCommands();

            Assert.IsFalse(commandService.TryGetCommand("hello", out _));
            Assert.IsFalse(commandService.TryGetCommand("", out _));
        }

        [TestMethod]
        public void CommandService_Supports_Custom_Identifier()
        {
            ICommandService commandService = CreateService(identifier: "//");

            commandService.RegisterCommands();

            Assert.IsTrue(commandService.TryGetCommand("//hello", out ICommand? command));
            Assert.AreEqual("hello", command.Name);
            Assert.IsFalse(commandService.TryGetCommand("!hello", out _));
            Assert.IsTrue(commandService.IsIdentifierOnlyMessage("//"));
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
            // Deliberately violates the ICommand contract: an empty description must be rejected.
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
