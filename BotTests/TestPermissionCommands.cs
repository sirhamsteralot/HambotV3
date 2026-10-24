using HambotV2.Commands;
using HambotV2.Connections;
using HambotV2.Commands.SystemCommands;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BotTests
{
    /// <summary>
    /// Behavior of !userpermission / !commandpermission against a real (in-memory) database:
    /// mention parsing (roadmap T7), tier validation and escalation guards (roadmap T16, FR-36/FR-37).
    /// </summary>
    [TestClass]
    public sealed class TestPermissionCommands
    {
        private const ulong GuildOwner = 100;
        private const ulong AdminUser = 200;
        private const ulong BotOwner = 250;
        private const ulong ModeratorUser = 300;
        private const ulong RandomUser = 400;
        private const ulong TargetUser = 500;

        private static DatabaseContext CreateUserDatabase(params (ulong id, int tier)[] users)
        {
            var database = DiscordTestDoubles.CreateDatabase();
            foreach (var (id, tier) in users)
                database.Users.Add(new DatabaseContext.User { DiscordId = id, PermissionTier = tier });
            database.SaveChanges();
            return database;
        }

        private static async Task<List<string>> ExecuteUserPermission(DatabaseContext database, string content, ulong authorId, ulong? guildOwnerId = null, ulong? botOwnerId = null)
        {
            var sentMessages = new List<string>();
            var message = guildOwnerId.HasValue
                ? DiscordTestDoubles.CreateGuildMessage(content, authorId, guildOwnerId.Value, sentMessages)
                : DiscordTestDoubles.CreateDirectMessage(content, authorId, sentMessages);
            var context = DiscordTestDoubles.CreateContext(database, guildOwnerId, botOwnerId);

            await new UserPermission().ExecuteCommand(message.Object, context);
            return sentMessages;
        }

        private static async Task<List<string>> ExecuteCommandPermission(DatabaseContext database, string content, ulong authorId, ulong? guildOwnerId = null, ulong? botOwnerId = null)
        {
            var sentMessages = new List<string>();
            var message = guildOwnerId.HasValue
                ? DiscordTestDoubles.CreateGuildMessage(content, authorId, guildOwnerId.Value, sentMessages)
                : DiscordTestDoubles.CreateDirectMessage(content, authorId, sentMessages);
            var context = DiscordTestDoubles.CreateContext(database, guildOwnerId, botOwnerId);

            await new CommandPermission().ExecuteCommand(message.Object, context);
            return sentMessages;
        }

        [TestMethod]
        public async Task UserPermission_Read_NoDatabaseRow_ShowsDefaultTier()
        {
            var database = DiscordTestDoubles.CreateDatabase();

            var replies = await ExecuteUserPermission(database, "!userpermission 500", authorId: AdminUser);

            StringAssert.Contains(replies[0], "Permission tier for user 500: 0");
        }

        [TestMethod]
        public async Task UserPermission_Read_Accepts_Mention()
        {
            var database = CreateUserDatabase((TargetUser, PermissionTier.Moderator));

            var replies = await ExecuteUserPermission(database, "!userpermission <@500>", authorId: AdminUser);

            StringAssert.Contains(replies[0], "Permission tier for user <@500>: 1");
        }

        [TestMethod]
        public async Task UserPermission_Read_Shows_GuildOwner_Tier()
        {
            var database = DiscordTestDoubles.CreateDatabase();

            var replies = await ExecuteUserPermission(database, "!userpermission <@100>", authorId: AdminUser, guildOwnerId: GuildOwner);

            StringAssert.Contains(replies[0], "Permission tier for user <@100>: 3");
            StringAssert.Contains(replies[0], "guild owner");
        }

        [TestMethod]
        public async Task UserPermission_Read_Shows_BotOwner_Tier()
        {
            var database = DiscordTestDoubles.CreateDatabase();

            var replies = await ExecuteUserPermission(database, "!userpermission <@250>", authorId: AdminUser, botOwnerId: BotOwner);

            StringAssert.Contains(replies[0], "Permission tier for user <@250>: 4");
            StringAssert.Contains(replies[0], "bot owner");
        }

        [TestMethod]
        public async Task UserPermission_Set_By_GuildOwner_Persists_Tier()
        {
            var database = DiscordTestDoubles.CreateDatabase();

            var replies = await ExecuteUserPermission(database, "!userpermission <@500> 1", authorId: GuildOwner, guildOwnerId: GuildOwner);

            StringAssert.Contains(replies[0], "Updated permission tier for user <@500> to 1");
            Assert.AreEqual(PermissionTier.Moderator, database.Users.Single(user => user.DiscordId == TargetUser).PermissionTier);
        }

        [TestMethod]
        public async Task UserPermission_Set_Accepts_Legacy_Mention_Form()
        {
            var database = DiscordTestDoubles.CreateDatabase();

            await ExecuteUserPermission(database, "!userpermission <@!500> 0", authorId: GuildOwner, guildOwnerId: GuildOwner);

            Assert.AreEqual(PermissionTier.Everyone, database.Users.Single(user => user.DiscordId == TargetUser).PermissionTier);
        }

        [TestMethod]
        public async Task UserPermission_Set_Updates_Existing_Row()
        {
            var database = CreateUserDatabase((TargetUser, PermissionTier.Everyone));

            await ExecuteUserPermission(database, "!userpermission 500 1", authorId: GuildOwner, guildOwnerId: GuildOwner);

            Assert.AreEqual(1, database.Users.Count());
            Assert.AreEqual(PermissionTier.Moderator, database.Users.Single(user => user.DiscordId == TargetUser).PermissionTier);
        }

        [TestMethod]
        public async Task UserPermission_Set_UnparseableUser_Rejected()
        {
            var database = DiscordTestDoubles.CreateDatabase();

            var replies = await ExecuteUserPermission(database, "!userpermission notauser 1", authorId: GuildOwner, guildOwnerId: GuildOwner);

            StringAssert.Contains(replies[0], "Could not parse a Discord user id from notauser");
            Assert.AreEqual(0, database.Users.Count());
        }

        [TestMethod]
        public async Task UserPermission_Set_Tier_Outside_Scale_Rejected()
        {
            var database = DiscordTestDoubles.CreateDatabase();

            var tooHigh = await ExecuteUserPermission(database, "!userpermission 500 5", authorId: GuildOwner, guildOwnerId: GuildOwner);
            var tooLow = await ExecuteUserPermission(database, "!userpermission 500 -1", authorId: GuildOwner, guildOwnerId: GuildOwner);

            StringAssert.Contains(tooHigh[0], "not a valid permission tier");
            StringAssert.Contains(tooLow[0], "not a valid permission tier");
            Assert.AreEqual(0, database.Users.Count());
        }

        [TestMethod]
        public async Task UserPermission_Set_BotOwner_Tier_Cannot_Be_Assigned_Even_By_BotOwner()
        {
            // Tier 4 comes from configuration only; even the bot owner cannot grant it.
            var database = DiscordTestDoubles.CreateDatabase();

            var replies = await ExecuteUserPermission(database, "!userpermission 500 4", authorId: BotOwner, botOwnerId: BotOwner);

            StringAssert.Contains(replies[0], "You can only assign permission tiers strictly below your own tier");
            Assert.AreEqual(0, database.Users.Count());
        }

        [TestMethod]
        public async Task UserPermission_Set_By_BotOwner_Can_Grant_Owner_Tier()
        {
            var database = DiscordTestDoubles.CreateDatabase();

            var replies = await ExecuteUserPermission(database, "!userpermission 500 3", authorId: BotOwner, botOwnerId: BotOwner);

            StringAssert.Contains(replies[0], "Updated permission tier for user 500 to 3");
            Assert.AreEqual(PermissionTier.Owner, database.Users.Single(user => user.DiscordId == TargetUser).PermissionTier);
        }

        [TestMethod]
        public async Task UserPermission_Set_By_BotOwner_Can_Modify_Guild_Owner()
        {
            var database = CreateUserDatabase((TargetUser, PermissionTier.Owner));

            var replies = await ExecuteUserPermission(database, "!userpermission 500 1", authorId: BotOwner, guildOwnerId: GuildOwner, botOwnerId: BotOwner);

            StringAssert.Contains(replies[0], "Updated permission tier for user 500 to 1");
            Assert.AreEqual(PermissionTier.Moderator, database.Users.Single(user => user.DiscordId == TargetUser).PermissionTier);
        }

        [TestMethod]
        public async Task UserPermission_Set_By_Admin_Cannot_Modify_Owner_Tier_User()
        {
            var database = CreateUserDatabase((AdminUser, PermissionTier.Admin), (TargetUser, PermissionTier.Owner));

            var replies = await ExecuteUserPermission(database, "!userpermission 500 0", authorId: AdminUser);

            StringAssert.Contains(replies[0], "You can only modify the permission tier of users below your own tier");
            Assert.AreEqual(PermissionTier.Owner, database.Users.Single(user => user.DiscordId == TargetUser).PermissionTier);
        }

        [TestMethod]
        public async Task UserPermission_Set_On_FreshGuild_RandomUser_Cannot_Grant_Any_Tier()
        {
            // Roadmap T16: without bootstrap, a tier-0 user has nobody below themselves and no
            // assignable tier — even tier 0 is rejected by the escalation guard.
            var database = DiscordTestDoubles.CreateDatabase();

            var replies = await ExecuteUserPermission(database, "!userpermission 500 0", authorId: RandomUser);

            Assert.AreEqual(1, replies.Count);
            StringAssert.Contains(replies[0], "You can only modify the permission tier of users below your own tier");
            Assert.AreEqual(0, database.Users.Count());
        }

        [TestMethod]
        public async Task UserPermission_Set_Own_Tier_Rejected()
        {
            var database = CreateUserDatabase((AdminUser, PermissionTier.Admin));

            var replies = await ExecuteUserPermission(database, "!userpermission 200 0", authorId: AdminUser);

            StringAssert.Contains(replies[0], "You cannot modify your own permission tier");
        }

        [TestMethod]
        public async Task UserPermission_Set_By_Moderator_Cannot_Promote_To_Admin()
        {
            var database = CreateUserDatabase((ModeratorUser, PermissionTier.Moderator), (TargetUser, PermissionTier.Everyone));

            var replies = await ExecuteUserPermission(database, "!userpermission 500 2", authorId: ModeratorUser);

            StringAssert.Contains(replies[0], "You can only assign permission tiers strictly below your own tier");
            Assert.AreEqual(PermissionTier.Everyone, database.Users.Single(user => user.DiscordId == TargetUser).PermissionTier);
        }

        [TestMethod]
        public async Task UserPermission_Set_By_Moderator_Cannot_Modify_Admin()
        {
            var database = CreateUserDatabase((ModeratorUser, PermissionTier.Moderator), (AdminUser, PermissionTier.Admin));

            var replies = await ExecuteUserPermission(database, "!userpermission 200 0", authorId: ModeratorUser);

            StringAssert.Contains(replies[0], "You can only modify the permission tier of users below your own tier");
            Assert.AreEqual(PermissionTier.Admin, database.Users.Single(user => user.DiscordId == AdminUser).PermissionTier);
        }

        [TestMethod]
        public async Task UserPermission_Set_By_Moderator_Cannot_Modify_Guild_Owner()
        {
            var database = CreateUserDatabase((ModeratorUser, PermissionTier.Moderator));

            var replies = await ExecuteUserPermission(database, "!userpermission <@100> 0", authorId: ModeratorUser, guildOwnerId: GuildOwner);

            StringAssert.Contains(replies[0], "You can only modify the permission tier of users below your own tier");
        }

        [TestMethod]
        public async Task UserPermission_Set_By_Admin_Cannot_Promote_To_Admin()
        {
            var database = CreateUserDatabase((AdminUser, PermissionTier.Admin), (TargetUser, PermissionTier.Everyone));

            var replies = await ExecuteUserPermission(database, "!userpermission 500 2", authorId: AdminUser);

            StringAssert.Contains(replies[0], "You can only assign permission tiers strictly below your own tier");
        }

        [TestMethod]
        public async Task UserPermission_Set_By_Admin_Can_Assign_Below_Own_Tier()
        {
            var database = CreateUserDatabase((AdminUser, PermissionTier.Admin), (TargetUser, PermissionTier.Everyone));

            var replies = await ExecuteUserPermission(database, "!userpermission 500 1", authorId: AdminUser);

            StringAssert.Contains(replies[0], "Updated permission tier for user 500 to 1");
            Assert.AreEqual(PermissionTier.Moderator, database.Users.Single(user => user.DiscordId == TargetUser).PermissionTier);
        }

        [TestMethod]
        public async Task UserPermission_InsufficientArguments_Shows_Usage()
        {
            var database = DiscordTestDoubles.CreateDatabase();

            var replies = await ExecuteUserPermission(database, "!userpermission", authorId: AdminUser);

            StringAssert.Contains(replies[0], "Insufficient arguments");
        }

        [TestMethod]
        public async Task CommandPermission_Read_NoOverride_Shows_Default_Hint()
        {
            var database = DiscordTestDoubles.CreateDatabase();

            var replies = await ExecuteCommandPermission(database, "!commandpermission hello", authorId: AdminUser);

            StringAssert.Contains(replies[0], "No permission tier override set for command hello");
        }

        [TestMethod]
        public async Task CommandPermission_Read_Shows_Override()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            database.Commands.Add(new DatabaseContext.CommandMetaData { CommandName = "hello", PermissionTier = PermissionTier.Moderator });
            database.SaveChanges();

            var replies = await ExecuteCommandPermission(database, "!commandpermission hello", authorId: AdminUser);

            StringAssert.Contains(replies[0], "Required permission tier for command hello: 1");
        }

        [TestMethod]
        public async Task CommandPermission_Set_By_Owner_Persists_Override()
        {
            var database = DiscordTestDoubles.CreateDatabase();

            var replies = await ExecuteCommandPermission(database, "!commandpermission hello 1", authorId: GuildOwner, guildOwnerId: GuildOwner);

            StringAssert.Contains(replies[0], "Updated required permission tier for command hello to 1");
            Assert.AreEqual(PermissionTier.Moderator, database.Commands.Single().PermissionTier);
        }

        [TestMethod]
        public async Task CommandPermission_Set_Tier_Outside_Scale_Rejected()
        {
            var database = DiscordTestDoubles.CreateDatabase();

            var replies = await ExecuteCommandPermission(database, "!commandpermission hello 9", authorId: GuildOwner, guildOwnerId: GuildOwner);

            StringAssert.Contains(replies[0], "not a valid permission tier");
            Assert.AreEqual(0, database.Commands.Count());
        }

        [TestMethod]
        public async Task CommandPermission_Set_NewTier_At_Or_Above_Own_Tier_Rejected()
        {
            var database = CreateUserDatabase((AdminUser, PermissionTier.Admin));

            var replies = await ExecuteCommandPermission(database, "!commandpermission hello 2", authorId: AdminUser);

            StringAssert.Contains(replies[0], "You can only assign permission tiers strictly below your own tier");
            Assert.AreEqual(0, database.Commands.Count());
        }

        [TestMethod]
        public async Task CommandPermission_Set_Command_At_Or_Above_Own_Tier_Rejected()
        {
            // The permission-management commands default to admin tier; an admin must not be able
            // to move them (that would hand the permission system to a lower tier).
            var database = CreateUserDatabase((AdminUser, PermissionTier.Admin));
            database.Commands.Add(new DatabaseContext.CommandMetaData { CommandName = "userpermission", PermissionTier = PermissionTier.Admin });
            database.SaveChanges();

            var replies = await ExecuteCommandPermission(database, "!commandpermission userpermission 0", authorId: AdminUser);

            StringAssert.Contains(replies[0], "You can only change the required tier of commands that currently sit below your own tier");
            Assert.AreEqual(PermissionTier.Admin, database.Commands.Single().PermissionTier);
        }

        [TestMethod]
        public async Task CommandPermission_Set_By_Owner_Can_Adjust_Admin_Gated_Command()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            database.Commands.Add(new DatabaseContext.CommandMetaData { CommandName = "userpermission", PermissionTier = PermissionTier.Admin });
            database.SaveChanges();

            var replies = await ExecuteCommandPermission(database, "!commandpermission userpermission 1", authorId: GuildOwner, guildOwnerId: GuildOwner);

            StringAssert.Contains(replies[0], "Updated required permission tier for command userpermission to 1");
            Assert.AreEqual(PermissionTier.Moderator, database.Commands.Single().PermissionTier);
        }

        [TestMethod]
        public async Task CommandPermission_Set_By_BotOwner_Can_Raise_Command_To_Owner_Tier()
        {
            // Command tiers are global (cross-guild) state; only the bot owner can gate a command at owner tier.
            var database = DiscordTestDoubles.CreateDatabase();

            var replies = await ExecuteCommandPermission(database, "!commandpermission hello 3", authorId: BotOwner, botOwnerId: BotOwner);

            StringAssert.Contains(replies[0], "Updated required permission tier for command hello to 3");
            Assert.AreEqual(PermissionTier.Owner, database.Commands.Single().PermissionTier);
        }

        [TestMethod]
        public async Task CommandPermission_InsufficientArguments_Shows_Usage()
        {
            var database = DiscordTestDoubles.CreateDatabase();

            var replies = await ExecuteCommandPermission(database, "!commandpermission", authorId: AdminUser);

            StringAssert.Contains(replies[0], "Insufficient arguments");
        }
    }
}
