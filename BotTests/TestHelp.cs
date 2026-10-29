using Discord;
using HambotV2.Commands;
using HambotV2.Commands.SystemCommands;
using HambotV2.Connections;
using Moq;
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
            const ulong invokerId = 77;

            var middle = HelpPagination.GetButtons(invokerId, currentPage: 2, pageCount: 3);
            Assert.AreEqual("help:page:77:1", middle!.Value.PreviousId);
            Assert.IsFalse(middle.Value.PreviousDisabled);
            Assert.AreEqual("help:page:77:3", middle.Value.NextId);
            Assert.IsFalse(middle.Value.NextDisabled);

            var first = HelpPagination.GetButtons(invokerId, 1, 3);
            Assert.IsTrue(first!.Value.PreviousDisabled, "The previous button is disabled on the first page");
            Assert.IsFalse(first.Value.NextDisabled);

            var last = HelpPagination.GetButtons(invokerId, 3, 3);
            Assert.IsFalse(last!.Value.PreviousDisabled);
            Assert.IsTrue(last.Value.NextDisabled, "The next button is disabled on the last page");

            Assert.IsNotNull(HelpPagination.BuildButtons(invokerId, 2, 3), "A multi-page list builds a button pair");
        }

        [TestMethod]
        public void Pagination_SinglePage_List_Has_No_Buttons()
        {
            Assert.IsNull(HelpPagination.GetButtons(77, 1, 1));
            Assert.IsNull(HelpPagination.BuildButtons(77, 1, 1));
        }

        [TestMethod]
        public void Pagination_CustomId_Parses_Back_To_The_Owner_And_Target_Page()
        {
            Assert.IsTrue(HelpPagination.TryGetTargetPage(HelpPagination.CustomId(77, 7), out ulong invokerId, out int page));
            Assert.AreEqual(77UL, invokerId);
            Assert.AreEqual(7, page);

            // The pre-ownership button format carries no invoker id: it parses as owned by nobody.
            Assert.IsTrue(HelpPagination.TryGetTargetPage("help:page:7", out ulong legacyInvokerId, out int legacyPage));
            Assert.AreEqual(0UL, legacyInvokerId);
            Assert.AreEqual(7, legacyPage);

            foreach (string foreign in new[] { "", "help:page:", "help:page:zero", "help:page:-1", "help:page:77:zero", "help:page:77:-1", "other:page:77:2", "helppage:77:3" })
                Assert.IsFalse(HelpPagination.TryGetTargetPage(foreign, out _, out _), $"Foreign custom id '{foreign}' must not parse as a help button");
        }

        /// <summary>Moq double of one pagination button click, wired for the click-handling tests.</summary>
        private static IPaginationButton CreateButtonClick(ulong clickerId, string customId, out Mock<IPaginationButton> button)
        {
            var user = new Mock<IUser>();
            user.SetupGet(u => u.Id).Returns(clickerId);

            button = new Mock<IPaginationButton>();
            button.SetupGet(b => b.User).Returns(user.Object);
            button.SetupGet(b => b.CustomId).Returns(customId);
            button.SetupGet(b => b.Channel).Returns(new Mock<IMessageChannel>().Object);
            button.SetupGet(b => b.CreatedAt).Returns(DateTimeOffset.UnixEpoch);
            button.Setup(b => b.DeferAsync()).Returns(Task.CompletedTask);
            button.Setup(b => b.SendEphemeralAsync(It.IsAny<string>())).Returns(Task.CompletedTask);
            button.Setup(b => b.ModifyListInPlaceAsync(It.IsAny<string>(), It.IsAny<MessageComponent>())).Returns(Task.CompletedTask);
            return button.Object;
        }

        [TestMethod]
        public async Task Pagination_Click_By_The_Invoker_Updates_The_Message_In_Place()
        {
            var button = CreateButtonClick(clickerId: 77, customId: HelpPagination.CustomId(77, 2), out Mock<IPaginationButton> click);
            var runner = new Mock<IVirtualCommandRunner>();
            MessageComponent components = HelpPagination.BuildButtons(77, 2, 3)!;
            runner
                .Setup(r => r.RunAsync(It.IsAny<VirtualCommandInvocation>()))
                .ReturnsAsync(new VirtualCommandResult(CommandOutcome.Executed, [new VirtualCommandReply("page 2 text", components)]));

            await HelpPagination.HandleClickAsync(button, runner.Object);

            click.Verify(b => b.DeferAsync(), Times.Once);
            click.Verify(b => b.ModifyListInPlaceAsync("page 2 text", components), Times.Once);
            click.Verify(b => b.SendEphemeralAsync(It.IsAny<string>()), Times.Never);
        }

        [TestMethod]
        public async Task Pagination_Click_By_Another_User_Is_Declined_Ephemerally_And_Leaves_The_Message_Alone()
        {
            var button = CreateButtonClick(clickerId: 300, customId: HelpPagination.CustomId(77, 2), out Mock<IPaginationButton> click);
            var runner = new Mock<IVirtualCommandRunner>();

            await HelpPagination.HandleClickAsync(button, runner.Object);

            click.Verify(b => b.DeferAsync(), Times.Once);
            click.Verify(b => b.SendEphemeralAsync(HelpPagination.ForeignClickerReply), Times.Once);
            click.Verify(b => b.ModifyListInPlaceAsync(It.IsAny<string>(), It.IsAny<MessageComponent>()), Times.Never);
            runner.Verify(r => r.RunAsync(It.IsAny<VirtualCommandInvocation>()), Times.Never,
                "A foreign click must not run the pipeline — no cooldown consumption, no audit event, no reply to capture");
        }

        [TestMethod]
        public async Task Pagination_Legacy_Button_Without_An_Owner_Is_Declined_For_Everyone()
        {
            var button = CreateButtonClick(clickerId: 77, customId: "help:page:2", out Mock<IPaginationButton> click);
            var runner = new Mock<IVirtualCommandRunner>();

            await HelpPagination.HandleClickAsync(button, runner.Object);

            click.Verify(b => b.SendEphemeralAsync(HelpPagination.ForeignClickerReply), Times.Once);
            click.Verify(b => b.ModifyListInPlaceAsync(It.IsAny<string>(), It.IsAny<MessageComponent>()), Times.Never);
            runner.Verify(r => r.RunAsync(It.IsAny<VirtualCommandInvocation>()), Times.Never);
        }

        [TestMethod]
        public async Task Pagination_Out_Of_Range_Click_Of_The_Invoker_Gets_An_Ephemeral_Reply_And_Keeps_The_Message()
        {
            var button = CreateButtonClick(clickerId: 77, customId: HelpPagination.CustomId(77, 5), out Mock<IPaginationButton> click);
            var runner = new Mock<IVirtualCommandRunner>();
            runner
                .Setup(r => r.RunAsync(It.IsAny<VirtualCommandInvocation>()))
                .ReturnsAsync(new VirtualCommandResult(CommandOutcome.Executed, [new VirtualCommandReply("There is no page 5; the command list has 1 page(s)", null)]));

            await HelpPagination.HandleClickAsync(button, runner.Object);

            click.Verify(b => b.SendEphemeralAsync("There is no page 5; the command list has 1 page(s)"), Times.Once);
            click.Verify(b => b.ModifyListInPlaceAsync(It.IsAny<string>(), It.IsAny<MessageComponent>()), Times.Never,
                "The out-of-range error must never replace the paginated message");
        }

        [TestMethod]
        public async Task Pagination_Declined_Pipeline_Invocation_Is_Ephemeral_And_Keeps_The_Message()
        {
            var button = CreateButtonClick(clickerId: 77, customId: HelpPagination.CustomId(77, 2), out Mock<IPaginationButton> click);
            var runner = new Mock<IVirtualCommandRunner>();
            runner
                .Setup(r => r.RunAsync(It.IsAny<VirtualCommandInvocation>()))
                .ReturnsAsync(new VirtualCommandResult(CommandOutcome.Throttled, [new VirtualCommandReply("You're using commands too quickly — please wait 3 more seconds.", null)]));

            await HelpPagination.HandleClickAsync(button, runner.Object);

            click.Verify(b => b.SendEphemeralAsync("You're using commands too quickly — please wait 3 more seconds."), Times.Once);
            click.Verify(b => b.ModifyListInPlaceAsync(It.IsAny<string>(), It.IsAny<MessageComponent>()), Times.Never);
        }

        private static List<string> Lines(int count) => Enumerable.Range(1, count).Select(number => $"!command{number} — description").ToList();

        [TestMethod]
        public void Every_Registered_Command_Carries_A_Defined_Category()
        {
            var commandService = CreateRegisteredService();

            foreach (ICommand command in commandService.Commands.Values)
                Assert.IsTrue(CommandCategories.IsValid(command.Category), $"Command '{command.Name}' must belong to exactly one defined category");
        }

        [TestMethod]
        public async Task Help_List_Shows_All_Groups_In_Category_Order_Alphabetized_Within()
        {
            var commandService = CreateRegisteredService();
            var database = DiscordTestDoubles.CreateDatabase();

            string reply = await ExecuteHelpAllPages(commandService, database, authorId: BotOwnerUser, guildOwnerId: GuildOwner, botOwnerId: BotOwnerUser);

            string[] headers = ["**General**", "**Quotes**", "**Custom responses**", "**Reminders**", "**Permissions & settings**", "**Backups & stability**", "**Bot profile**"];
            int previous = -1;
            foreach (string header in headers)
            {
                int position = reply.IndexOf(header, StringComparison.Ordinal);
                Assert.IsTrue(position >= 0, $"The bot owner sees every command, so the group '{header}' must render");
                Assert.IsTrue(position > previous, $"The group '{header}' must render after the previous category's group");
                previous = position;
            }

            Assert.IsTrue(reply.IndexOf("!importquotes", StringComparison.Ordinal) < reply.IndexOf("!quote —", StringComparison.Ordinal), "Commands within a group are alphabetized");
            Assert.IsTrue(reply.IndexOf("!quote —", StringComparison.Ordinal) < reply.IndexOf("!removequote", StringComparison.Ordinal), "Commands within a group are alphabetized");
            Assert.IsTrue(reply.IndexOf("!removequote", StringComparison.Ordinal) < reply.IndexOf("!searchquotes", StringComparison.Ordinal), "Commands within a group are alphabetized");
        }

        [TestMethod]
        public async Task Help_List_Omits_Group_Headers_With_No_Visible_Commands()
        {
            var commandService = CreateRegisteredService();
            var database = DiscordTestDoubles.CreateDatabase();

            string reply = await ExecuteHelpAllPages(commandService, database, authorId: RandomUser, guildOwnerId: GuildOwner);

            StringAssert.Contains(reply, "**General**");
            StringAssert.Contains(reply, "**Quotes**");
            StringAssert.Contains(reply, "**Custom responses**");
            StringAssert.Contains(reply, "**Reminders**");
            Assert.IsFalse(reply.Contains("**Permissions & settings**"), "A group with no command visible to the user must not render its header");
            Assert.IsFalse(reply.Contains("**Backups & stability**"), "A group with no command visible to the user must not render its header");
            Assert.IsFalse(reply.Contains("**Bot profile**"), "A group with no command visible to the user must not render its header");
        }

        [TestMethod]
        public async Task Help_List_Empties_A_Group_When_Tier_Filtering_Removes_Its_Last_Command()
        {
            var commandService = CreateRegisteredService();
            var database = DiscordTestDoubles.CreateDatabase();
            database.Commands.Add(new DatabaseContext.CommandMetaData { GuildId = DiscordTestDoubles.DefaultGuildId, CommandName = "quote", PermissionTier = PermissionTier.Owner });
            database.Commands.Add(new DatabaseContext.CommandMetaData { GuildId = DiscordTestDoubles.DefaultGuildId, CommandName = "searchquotes", PermissionTier = PermissionTier.Owner });
            database.SaveChanges();

            string reply = await ExecuteHelp(commandService, database, "!help", authorId: RandomUser, guildOwnerId: GuildOwner);

            Assert.IsFalse(reply.Contains("!quote"), "Raised-tier commands stay hidden");
            Assert.IsFalse(reply.Contains("**Quotes**"), "The Quotes group is emptied entirely and must not render its header");
        }

        [TestMethod]
        public void BuildPages_Keeps_A_Group_Whole_When_It_Fits_A_Page()
        {
            var groups = new List<CommandGroup>
            {
                new(CommandCategory.General, Lines(6)),
                new(CommandCategory.Quotes, Lines(5)),
            };

            List<string> pages = HelpCommand.BuildPages(groups);

            Assert.AreEqual(2, pages.Count, "6 + 5 commands do not fit one 10-command page, so the second group moves whole to the next page");
            Assert.IsTrue(pages[0].Contains("**General**", StringComparison.Ordinal));
            Assert.IsFalse(pages[0].Contains("**Quotes**", StringComparison.Ordinal), "A page break must not split a group that fits whole");
            Assert.IsTrue(pages[1].StartsWith("**Quotes**", StringComparison.Ordinal));
        }

        [TestMethod]
        public void BuildPages_Fills_A_Page_With_Whole_Groups()
        {
            var groups = new List<CommandGroup>
            {
                new(CommandCategory.General, Lines(3)),
                new(CommandCategory.Quotes, Lines(4)),
                new(CommandCategory.CustomResponses, Lines(3)),
                new(CommandCategory.Reminders, Lines(3)),
            };

            List<string> pages = HelpCommand.BuildPages(groups);

            Assert.AreEqual(2, pages.Count, "3 + 4 + 3 = 10 commands fill the first page; the fourth group starts the next");
            StringAssert.Contains(pages[0], "**Custom responses**");
            Assert.IsFalse(pages[0].Contains("**Reminders**", StringComparison.Ordinal));
            Assert.IsTrue(pages[1].StartsWith("**Reminders**", StringComparison.Ordinal));
        }

        [TestMethod]
        public void BuildPages_Moves_A_Group_To_The_Next_Page_On_Character_Overflow()
        {
            var groups = new List<CommandGroup>
            {
                new(CommandCategory.General, [new string('a', 100)]),
                new(CommandCategory.Quotes, [new string('b', 100), new string('c', 100)]),
            };

            List<string> pages = HelpCommand.BuildPages(groups, maxPageLength: 250);

            Assert.AreEqual(2, pages.Count, "The second group does not fit the remaining character budget and moves whole to the next page");
            Assert.AreEqual($"**General**{Environment.NewLine}{new string('a', 100)}", pages[0]);
            Assert.AreEqual($"**Quotes**{Environment.NewLine}{new string('b', 100)}{Environment.NewLine}{new string('c', 100)}", pages[1]);
        }

        [TestMethod]
        public void BuildPages_Splits_An_Oversized_Group_And_Repeats_Its_Header()
        {
            var group = new CommandGroup(CommandCategory.General, Lines(15));

            List<string> pages = HelpCommand.BuildPages([group]);

            Assert.AreEqual(2, pages.Count, "A group that alone exceeds a page's command budget spans two pages");
            Assert.AreEqual(11, pages[0].Split(Environment.NewLine).Length, "Header plus the 10 commands that fit");
            Assert.AreEqual(6, pages[1].Split(Environment.NewLine).Length, "Header repeated, plus the remaining commands");
            Assert.IsTrue(pages[1].StartsWith("**General**", StringComparison.Ordinal), "A group spanning pages repeats its header on each continuation page");
        }

        [TestMethod]
        public void BuildPages_Empty_Input_Has_No_Pages()
        {
            List<string> pages = HelpCommand.BuildPages([]);

            Assert.AreEqual(0, pages.Count);
        }
    }
}
