using HambotV2.Commands;
using HambotV2.Connections;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BotTests
{
    /// <summary>
    /// Behavior of the !help command (FR-38 to FR-40): tier-filtered listing,
    /// detailed help, and the generic non-revealing reply for unknown/insufficient-tier commands.
    /// </summary>
    [TestClass]
    public sealed class TestHelp
    {
        private const ulong GuildOwner = 100;
        private const ulong AdminUser = 200;
        private const ulong RandomUser = 300;

        private static CommandService CreateRegisteredService()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> {
                    {"BotConfiguration:CommandIdentifier", "!"},
                })
                .Build();

            var commandService = new CommandService(Mock.Of<ILogger<CommandService>>(), configuration);
            commandService.RegisterCommands();
            return commandService;
        }

        private static async Task<string> ExecuteHelp(CommandService commandService, DatabaseContext database, string content, ulong authorId, ulong? guildOwnerId = null, ulong? guildId = DiscordTestDoubles.DefaultGuildId)
        {
            var sentMessages = new List<string>();
            var message = guildOwnerId.HasValue
                ? DiscordTestDoubles.CreateGuildMessage(content, authorId, guildOwnerId.Value, sentMessages)
                : DiscordTestDoubles.CreateDirectMessage(content, authorId, sentMessages);
            var context = DiscordTestDoubles.CreateContext(database, guildOwnerId, botOwnerId: null, guildId);

            await commandService.Commands["help"].ExecuteCommand(message.Object, context);
            return sentMessages[0];
        }

        [TestMethod]
        public async Task Help_List_For_Tier0_User_Shows_Only_Everyone_Commands()
        {
            var commandService = CreateRegisteredService();
            var database = DiscordTestDoubles.CreateDatabase();

            string reply = await ExecuteHelp(commandService, database, "!help", authorId: RandomUser);

            StringAssert.Contains(reply, "!help");
            StringAssert.Contains(reply, "!hello");
            Assert.IsFalse(reply.Contains("!userpermission"), "A tier-0 user must not see admin commands");
            Assert.IsFalse(reply.Contains("!commandpermission"), "A tier-0 user must not see admin commands");
        }

        [TestMethod]
        public async Task Help_List_For_Admin_User_Shows_Admin_Commands()
        {
            var commandService = CreateRegisteredService();
            var database = DiscordTestDoubles.CreateDatabase();
            database.Users.Add(new DatabaseContext.User { GuildId = DiscordTestDoubles.DefaultGuildId, DiscordId = AdminUser, PermissionTier = PermissionTier.Admin });
            database.SaveChanges();

            string reply = await ExecuteHelp(commandService, database, "!help", authorId: AdminUser);

            StringAssert.Contains(reply, "!help");
            StringAssert.Contains(reply, "!hello");
            StringAssert.Contains(reply, "!userpermission");
            StringAssert.Contains(reply, "!commandpermission");
        }

        [TestMethod]
        public async Task Help_List_For_Guild_Owner_Shows_Admin_Commands_Without_Database_Row()
        {
            // Bootstrap admin: the guild owner sees everything even on a fresh guild.
            var commandService = CreateRegisteredService();
            var database = DiscordTestDoubles.CreateDatabase();

            string reply = await ExecuteHelp(commandService, database, "!help", authorId: GuildOwner, guildOwnerId: GuildOwner);

            StringAssert.Contains(reply, "!userpermission");
            StringAssert.Contains(reply, "!commandpermission");
        }

        [TestMethod]
        public async Task Help_List_Respects_Command_Tier_Overrides()
        {
            var commandService = CreateRegisteredService();
            var database = DiscordTestDoubles.CreateDatabase();
            database.Commands.Add(new DatabaseContext.CommandMetaData { GuildId = DiscordTestDoubles.DefaultGuildId, CommandName = "hello", PermissionTier = PermissionTier.Owner });
            database.SaveChanges();

            string reply = await ExecuteHelp(commandService, database, "!help", authorId: RandomUser);

            Assert.IsFalse(reply.Contains("!hello"), "A tier-0 user must not see a command raised to owner tier via the database");
            StringAssert.Contains(reply, "!help");
        }

        [TestMethod]
        public async Task Help_Detail_For_Permitted_Command_Shows_Description_Usage_And_Tier()
        {
            var commandService = CreateRegisteredService();
            var database = DiscordTestDoubles.CreateDatabase();

            string reply = await ExecuteHelp(commandService, database, "!help hello", authorId: RandomUser);

            StringAssert.Contains(reply, "!hello — Greets the channel");
            StringAssert.Contains(reply, "Usage: !hello");
            StringAssert.Contains(reply, "Required permission: everyone (tier 0)");
        }

        [TestMethod]
        public async Task Help_Detail_For_Command_Above_User_Tier_Gives_Generic_Reply()
        {
            var commandService = CreateRegisteredService();
            var database = DiscordTestDoubles.CreateDatabase();

            string reply = await ExecuteHelp(commandService, database, "!help userpermission", authorId: RandomUser);

            Assert.AreEqual("The requested command is unavailable", reply);
        }

        [TestMethod]
        public async Task Help_Detail_For_Unknown_Command_Gives_Same_Generic_Reply()
        {
            var commandService = CreateRegisteredService();
            var database = DiscordTestDoubles.CreateDatabase();

            string reply = await ExecuteHelp(commandService, database, "!help nosuchcommand", authorId: RandomUser);

            Assert.AreEqual("The requested command is unavailable", reply);
        }

        [TestMethod]
        public async Task Help_Detail_Generic_Reply_Does_Not_Leak_Whether_Command_Exists()
        {
            // FR-40: the reply for an insufficient-tier command must be indistinguishable from the
            // reply for a truly unknown command.
            var commandService = CreateRegisteredService();
            var database = DiscordTestDoubles.CreateDatabase();

            string insufficientTier = await ExecuteHelp(commandService, database, "!help userpermission", authorId: RandomUser);
            string unknown = await ExecuteHelp(commandService, database, "!help nosuchcommand", authorId: RandomUser);

            Assert.AreEqual(unknown, insufficientTier);
        }

        [TestMethod]
        public async Task Help_Detail_For_Admin_Command_Shown_To_Admin()
        {
            var commandService = CreateRegisteredService();
            var database = DiscordTestDoubles.CreateDatabase();
            database.Users.Add(new DatabaseContext.User { GuildId = DiscordTestDoubles.DefaultGuildId, DiscordId = AdminUser, PermissionTier = PermissionTier.Admin });
            database.SaveChanges();

            string reply = await ExecuteHelp(commandService, database, "!help userpermission", authorId: AdminUser);

            StringAssert.Contains(reply, "Usage: !userpermission <user> [tier]");
            StringAssert.Contains(reply, "Required permission: admin (tier 2)");
        }

        [TestMethod]
        public async Task Help_In_DirectMessage_Shows_Commands_At_Default_Tiers()
        {
            // DM scope (FR-25): the user acts at owner tier and command tiers are their built-in
            // defaults, so a random user sees every command whose default is at most owner.
            var commandService = CreateRegisteredService();
            var database = DiscordTestDoubles.CreateDatabase();

            string reply = await ExecuteHelp(commandService, database, "!help", authorId: RandomUser, guildOwnerId: null, guildId: null);

            StringAssert.Contains(reply, "!hello");
            StringAssert.Contains(reply, "!help");
            StringAssert.Contains(reply, "!userpermission", "Default admin tier is below the DM owner tier, so the command must be listed");
        }

        [TestMethod]
        public async Task Help_Detail_In_DirectMessage_Shows_Default_Tier()
        {
            var commandService = CreateRegisteredService();
            var database = DiscordTestDoubles.CreateDatabase();

            string reply = await ExecuteHelp(commandService, database, "!help userpermission", authorId: RandomUser, guildOwnerId: null, guildId: null);

            StringAssert.Contains(reply, "Usage: !userpermission <user> [tier]");
            StringAssert.Contains(reply, "Required permission: admin (tier 2)");
        }
    }
}
