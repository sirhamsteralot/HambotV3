using Discord;
using HambotV2.Commands;
using HambotV2.Commands.SystemCommands;
using HambotV2.Connections;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BotTests
{
    [TestClass]
    public sealed class TestHelp
    {
        private const ulong GuildOwner = 100;
        private const ulong AdminUser = 200;
        private const ulong RandomUser = 300;
        private const ulong BotOwnerUser = 400;

        private static CommandService CreateRegisteredService()
        {
            var commandService = new CommandService();
            commandService.RegisterCommands();
            return commandService;
        }

        private static async Task<List<string>> ExecuteHelpAll(CommandService commandService, DatabaseContext database, string content, ulong authorId, ulong? guildOwnerId = null, ulong? botOwnerId = null, ulong? guildId = DiscordTestDoubles.DefaultGuildId)
        {
            var sentMessages = new List<string>();
            var message = guildOwnerId.HasValue
                ? DiscordTestDoubles.CreateGuildMessage(content, authorId, guildOwnerId.Value, sentMessages)
                : DiscordTestDoubles.CreateDirectMessage(content, authorId, sentMessages);
            var context = DiscordTestDoubles.CreateContext(database, guildOwnerId, botOwnerId, guildId);

            await commandService.Commands["help"].ExecuteCommand(message.Object, context);
            return sentMessages;
        }

        private static async Task<string> ExecuteHelp(CommandService commandService, DatabaseContext database, string content, ulong authorId, ulong? guildOwnerId = null, ulong? botOwnerId = null, ulong? guildId = DiscordTestDoubles.DefaultGuildId)
        {
            List<string> sentMessages = await ExecuteHelpAll(commandService, database, content, authorId, guildOwnerId, botOwnerId, guildId);
            return sentMessages[0];
        }

        /// <summary>The full list for the user: every page concatenated, so visibility assertions do not depend on the page size.</summary>
        private static async Task<string> ExecuteHelpAllPages(CommandService commandService, DatabaseContext database, ulong authorId, ulong? guildOwnerId = null, ulong? botOwnerId = null, ulong? guildId = DiscordTestDoubles.DefaultGuildId)
        {
            string page = await ExecuteHelp(commandService, database, "!help", authorId, guildOwnerId, botOwnerId, guildId);
            string allPages = page;

            if (!page.Contains("page 1 of ", StringComparison.Ordinal))
                return allPages;

            int pageCount = int.Parse(page.Split("page 1 of ")[1].Split(')')[0]);
            for (int pageNumber = 2; pageNumber <= pageCount; pageNumber++)
                allPages += Environment.NewLine + await ExecuteHelp(commandService, database, $"!help {pageNumber}", authorId, guildOwnerId, botOwnerId, guildId);

            return allPages;
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

            string reply = await ExecuteHelpAllPages(commandService, database, authorId: AdminUser);

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

            string reply = await ExecuteHelpAllPages(commandService, database, authorId: GuildOwner, guildOwnerId: GuildOwner);

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
            var commandService = CreateRegisteredService();
            var database = DiscordTestDoubles.CreateDatabase();

            string reply = await ExecuteHelpAllPages(commandService, database, authorId: RandomUser, guildOwnerId: null, guildId: null);

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

        [TestMethod]
        public async Task Help_List_For_Bot_Owner_Is_Paginated_With_Page_Hint()
        {
            // The bot owner sees every command, which exceeds one page's character budget.
            var commandService = CreateRegisteredService();
            var database = DiscordTestDoubles.CreateDatabase();

            string page1 = await ExecuteHelp(commandService, database, "!help", authorId: BotOwnerUser, guildOwnerId: GuildOwner, botOwnerId: BotOwnerUser);

            StringAssert.Contains(page1, "page 1 of");
            StringAssert.Contains(page1, "!help <pageNumber> to see another page");
            Assert.IsTrue(page1.Length <= 2000, "A page must fit within Discord's 2000-character message limit");

            string page2 = await ExecuteHelp(commandService, database, "!help 2", authorId: BotOwnerUser, guildOwnerId: GuildOwner, botOwnerId: BotOwnerUser);

            StringAssert.Contains(page2, "page 2 of");
            var page1Lines = page1.Split(Environment.NewLine).Where(line => line.Contains(" — ")).ToList();
            var page2Lines = page2.Split(Environment.NewLine).Where(line => line.Contains(" — ")).ToList();
            Assert.IsTrue(page2Lines.Count > 0, "The second page must show the remaining commands");
            Assert.IsFalse(page2Lines.Any(page1Lines.Contains), "Pages must be disjoint; page 2 shows commands that did not fit on page 1");
        }

        [TestMethod]
        public async Task Help_List_For_Low_Tier_User_Stays_On_One_Page_Without_Indicator()
        {
            var commandService = CreateRegisteredService();
            var database = DiscordTestDoubles.CreateDatabase();

            List<string> replies = await ExecuteHelpAll(commandService, database, "!help", authorId: RandomUser, guildOwnerId: GuildOwner);

            Assert.AreEqual(1, replies.Count);
            Assert.IsFalse(replies[0].Contains("page 1 of"), "A single page must keep the old header without a page indicator");
            StringAssert.Contains(replies[0], "Available commands:");
        }

        [TestMethod]
        public async Task Help_Page_Argument_Shows_Requested_Page()
        {
            var commandService = CreateRegisteredService();
            var database = DiscordTestDoubles.CreateDatabase();

            string reply = await ExecuteHelp(commandService, database, "!help 2", authorId: BotOwnerUser, guildOwnerId: GuildOwner, botOwnerId: BotOwnerUser);

            StringAssert.Contains(reply, "page 2 of");
            StringAssert.Contains(reply, "!help <pageNumber> to see another page");
        }

        [TestMethod]
        public async Task Help_Page_Out_Of_Range_Gives_Friendly_Reply()
        {
            var commandService = CreateRegisteredService();
            var database = DiscordTestDoubles.CreateDatabase();

            string reply = await ExecuteHelp(commandService, database, "!help 99", authorId: RandomUser, guildOwnerId: GuildOwner);

            Assert.AreEqual("There is no page 99; the command list has 1 page(s)", reply);
        }

        [TestMethod]
        public async Task Help_Page_Below_One_Gives_Friendly_Reply()
        {
            var commandService = CreateRegisteredService();
            var database = DiscordTestDoubles.CreateDatabase();

            string reply = await ExecuteHelp(commandService, database, "!help 0", authorId: RandomUser, guildOwnerId: GuildOwner);

            StringAssert.Contains(reply, "There is no page 0");
        }

        [TestMethod]
        public async Task Help_MultiPage_List_Attaches_Pagination_Buttons()
        {
            // The bot owner sees every command, so the list paginates and carries the button pair.
            var commandService = CreateRegisteredService();
            var database = DiscordTestDoubles.CreateDatabase();
            var sentMessages = new List<string>();
            var sentComponents = new List<MessageComponent?>();

            var message = DiscordTestDoubles.CreateGuildMessage("!help 2", BotOwnerUser, GuildOwner, sentMessages, sentComponents: sentComponents);
            var context = DiscordTestDoubles.CreateContext(database, GuildOwner, BotOwnerUser);

            await commandService.Commands["help"].ExecuteCommand(message.Object, context);

            StringAssert.Contains(sentMessages[0], "page 2 of");
            Assert.IsNotNull(sentComponents[0], "A multi-page list attaches the previous/next buttons");
        }

        [TestMethod]
        public async Task Help_SinglePage_List_Has_No_Pagination_Buttons()
        {
            var commandService = CreateRegisteredService();
            var database = DiscordTestDoubles.CreateDatabase();
            var sentMessages = new List<string>();
            var sentComponents = new List<MessageComponent?>();

            var message = DiscordTestDoubles.CreateGuildMessage("!help", RandomUser, GuildOwner, sentMessages, sentComponents: sentComponents);
            var context = DiscordTestDoubles.CreateContext(database, GuildOwner);

            await commandService.Commands["help"].ExecuteCommand(message.Object, context);

            Assert.IsNull(sentComponents[0], "A single-page list has no buttons to attach");
        }

        [TestMethod]
        public void Pagination_Buttons_Target_Adjacent_Pages_And_Disable_At_The_Bounds()
        {
            var middle = HelpPagination.GetButtons(currentPage: 2, pageCount: 3);
            Assert.AreEqual("help:page:1", middle!.Value.PreviousId);
            Assert.IsFalse(middle.Value.PreviousDisabled);
            Assert.AreEqual("help:page:3", middle.Value.NextId);
            Assert.IsFalse(middle.Value.NextDisabled);

            var first = HelpPagination.GetButtons(1, 3);
            Assert.IsTrue(first!.Value.PreviousDisabled, "The previous button is disabled on the first page");
            Assert.IsFalse(first.Value.NextDisabled);

            var last = HelpPagination.GetButtons(3, 3);
            Assert.IsFalse(last!.Value.PreviousDisabled);
            Assert.IsTrue(last.Value.NextDisabled, "The next button is disabled on the last page");

            Assert.IsNotNull(HelpPagination.BuildButtons(2, 3), "A multi-page list builds a button pair");
        }

        [TestMethod]
        public void Pagination_SinglePage_List_Has_No_Buttons()
        {
            Assert.IsNull(HelpPagination.GetButtons(1, 1));
            Assert.IsNull(HelpPagination.BuildButtons(1, 1));
        }

        [TestMethod]
        public void Pagination_CustomId_Parses_Back_To_The_Target_Page()
        {
            Assert.IsTrue(HelpPagination.TryGetTargetPage(HelpPagination.CustomId(7), out int page));
            Assert.AreEqual(7, page);

            foreach (string foreign in new[] { "", "help:page:", "help:page:zero", "help:page:-1", "other:page:2", "helppage:3" })
                Assert.IsFalse(HelpPagination.TryGetTargetPage(foreign, out _), $"Foreign custom id '{foreign}' must not parse as a help button");
        }

        [TestMethod]
        public void BuildPages_Caps_At_Ten_Commands_Per_Page()
        {
            var lines = Enumerable.Range(1, 25).Select(number => $"!command{number} — description").ToList();

            List<string> pages = HelpCommand.BuildPages(lines);

            Assert.AreEqual(3, pages.Count, "25 commands split into 10 + 10 + 5");
            Assert.AreEqual(10, pages[0].Split(Environment.NewLine).Length);
            Assert.AreEqual(10, pages[1].Split(Environment.NewLine).Length);
            Assert.AreEqual(5, pages[2].Split(Environment.NewLine).Length);
        }

        [TestMethod]
        public void BuildPages_Splits_Lines_By_Character_Budget_In_Order()
        {
            var lines = new List<string> { new string('a', 100), new string('b', 100), new string('c', 100) };

            List<string> pages = HelpCommand.BuildPages(lines, maxPageLength: 250);

            Assert.AreEqual(2, pages.Count, "Two 100-char lines fit into a 250-char page, the third starts a new one");
            Assert.AreEqual(new string('a', 100) + Environment.NewLine + new string('b', 100), pages[0]);
            Assert.AreEqual(new string('c', 100), pages[1]);
        }

        [TestMethod]
        public void BuildPages_Oversized_Line_Still_Forms_Own_Page()
        {
            var lines = new List<string> { new string('a', 300) };

            List<string> pages = HelpCommand.BuildPages(lines, maxPageLength: 100);

            Assert.AreEqual(1, pages.Count);
            Assert.AreEqual(new string('a', 300), pages[0]);
        }

        [TestMethod]
        public void BuildPages_Empty_Input_Has_No_Pages()
        {
            List<string> pages = HelpCommand.BuildPages([]);

            Assert.AreEqual(0, pages.Count);
        }
    }
}
