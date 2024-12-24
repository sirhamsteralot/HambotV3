using HambotV2.Commands;
using HambotV2.Commands.SystemCommands;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BotTests
{
    [TestClass]
    public sealed class TestCommandInfrastructure
    {
        [TestMethod]
        public void CommandService_Register_Command_HelloWorld()
        {
            var mock = new Mock<ILogger<CommandService>>();
            ILogger<CommandService> logger = mock.Object;

            var inMemorySettings = new Dictionary<string, string> {
                {"BotConfiguration:CommandIdentifier", "!"},
            };

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(inMemorySettings)
                .Build();

            ICommandService commandService = new CommandService(logger, configuration);

            Assert.IsTrue(commandService.Commands.Count == 0);

            commandService.RegisterCommand("hello", new HelloWorld());

            Assert.IsTrue(commandService.Commands.Count > 0);
        }

        [TestMethod]
        public void CommandService_Register_All_Commands()
        {
            var mock = new Mock<ILogger<CommandService>>();
            ILogger<CommandService> logger = mock.Object;

            var inMemorySettings = new Dictionary<string, string> {
                {"BotConfiguration:CommandIdentifier", "!"},
            };

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(inMemorySettings)
                .Build();

            ICommandService commandService = new CommandService(logger, configuration);

            Assert.IsTrue(commandService.Commands.Count == 0);

            commandService.RegisterCommands();

            Assert.IsTrue(commandService.CommandsRegistered);
            Assert.IsTrue(commandService.Commands.Count > 0);
        }

        [TestMethod]
        public void CommandService_Get_Helloworld_Command_Success()
        {
            var mock = new Mock<ILogger<CommandService>>();
            ILogger<CommandService> logger = mock.Object;

            var inMemorySettings = new Dictionary<string, string> {
                {"BotConfiguration:CommandIdentifier", "!"},
            };

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(inMemorySettings)
                .Build();

            ICommandService commandService = new CommandService(logger, configuration);

            Assert.IsTrue(commandService.Commands.Count == 0);

            commandService.RegisterCommands();

            Assert.IsTrue(commandService.CommandsRegistered);
            Assert.IsTrue(commandService.Commands.Count > 0);

            bool returnValue = commandService.TryGetCommand("!hello", out ICommand command);
            Assert.IsTrue(returnValue);
            Assert.IsNotNull(command);
        }

        [TestMethod]
        public void CommandService_Get_Invalid_Command()
        {
            var mock = new Mock<ILogger<CommandService>>();
            ILogger<CommandService> logger = mock.Object;

            var inMemorySettings = new Dictionary<string, string> {
                {"BotConfiguration:CommandIdentifier", "!"},
            };

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(inMemorySettings)
                .Build();

            ICommandService commandService = new CommandService(logger, configuration);

            Assert.IsTrue(commandService.Commands.Count == 0);

            commandService.RegisterCommands();

            Assert.IsTrue(commandService.CommandsRegistered);
            Assert.IsTrue(commandService.Commands.Count > 0);

            bool returnValue = commandService.TryGetCommand("!gibberishcommandlaksjf;lsakjdf;laskjdf;alk asdfa", out ICommand command);
            Assert.IsFalse(returnValue);
            Assert.IsNull(command);
        }
    }
}
