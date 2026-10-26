using HambotV2.Commands;
using HambotV2.Connections;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BotTests
{
    [TestClass]
    public sealed class TestPermissions
    {
        private const ulong GuildOwner = 100;
        private const ulong BotOwner = 250;
        private const ulong RandomUser = 300;
        private const ulong GuildId = 1;
        private const ulong OtherGuildId = 2;

        [TestMethod]
        public void PermissionTier_Scale_Is_Validated()
        {
            Assert.IsTrue(PermissionTier.IsValid(0));
            Assert.IsTrue(PermissionTier.IsValid(PermissionTier.BotOwner));
            Assert.IsFalse(PermissionTier.IsValid(-1));
            Assert.IsFalse(PermissionTier.IsValid(PermissionTier.BotOwner + 1));
        }

        [TestMethod]
        public void Bootstrap_Guild_Owner_Acts_At_Owner_Tier()
        {
            var overrides = new TierOverrides { GuildOwnerId = GuildOwner };

            Assert.AreEqual(PermissionTier.Owner, overrides.EffectiveTierOf(GuildOwner, storedTier: 0));
        }

        [TestMethod]
        public void Configured_BotOwner_Acts_At_BotOwner_Tier()
        {
            var overrides = new TierOverrides { GuildOwnerId = GuildOwner, BotOwnerId = BotOwner };

            Assert.AreEqual(PermissionTier.BotOwner, overrides.EffectiveTierOf(BotOwner, storedTier: 0));
        }

        [TestMethod]
        public void BotOwner_Tier_Wins_Over_Guild_Owner_Bootstrap()
        {
            var overrides = new TierOverrides { GuildOwnerId = BotOwner, BotOwnerId = BotOwner };

            Assert.AreEqual(PermissionTier.BotOwner, overrides.EffectiveTierOf(BotOwner, storedTier: 0));
        }

        [TestMethod]
        public void Regular_User_Keeps_Stored_Tier()
        {
            var overrides = new TierOverrides { GuildOwnerId = GuildOwner, BotOwnerId = BotOwner };

            Assert.AreEqual(PermissionTier.Moderator, overrides.EffectiveTierOf(RandomUser, storedTier: PermissionTier.Moderator));
        }

        [TestMethod]
        public async Task FreshGuild_RandomUser_Cannot_Run_Permission_Management_Commands()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var permissionsService = new PermissionsService();
            var userPermission = DiscordTestDoubles.CreateMockCommand("userpermission", defaultPermissionTier: PermissionTier.Admin);

            Assert.IsFalse((await permissionsService.CheckUserPermissions(RandomUser, GuildId, TierOverrides.None, userPermission.Object, database)).IsAllowed);
        }

        [TestMethod]
        public async Task FreshGuild_GuildOwner_Can_Run_Permission_Management_Commands()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var permissionsService = new PermissionsService();
            var userPermission = DiscordTestDoubles.CreateMockCommand("userpermission", defaultPermissionTier: PermissionTier.Admin);

            Assert.IsTrue((await permissionsService.CheckUserPermissions(GuildOwner, GuildId, new TierOverrides { GuildOwnerId = GuildOwner }, userPermission.Object, database)).IsAllowed);
        }

        [TestMethod]
        public async Task FreshGuild_BotOwner_Can_Run_Permission_Management_Commands()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var permissionsService = new PermissionsService();
            var userPermission = DiscordTestDoubles.CreateMockCommand("userpermission", defaultPermissionTier: PermissionTier.Admin);

            Assert.IsTrue((await permissionsService.CheckUserPermissions(BotOwner, GuildId, new TierOverrides { BotOwnerId = BotOwner }, userPermission.Object, database)).IsAllowed);
        }

        [TestMethod]
        public async Task Command_Required_Tier_Missing_Row_Falls_Back_To_Command_Default()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var permissionsService = new PermissionsService();
            var command = DiscordTestDoubles.CreateMockCommand("hello", defaultPermissionTier: 0);

            Assert.AreEqual(0, await permissionsService.GetCommandRequiredTier(GuildId, command.Object, database));
        }

        [TestMethod]
        public async Task Command_Required_Tier_Database_Override_Wins_Over_Default()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            database.Commands.Add(new DatabaseContext.CommandMetaData { GuildId = GuildId, CommandName = "hello", PermissionTier = PermissionTier.Owner });
            database.SaveChanges();

            var permissionsService = new PermissionsService();
            var command = DiscordTestDoubles.CreateMockCommand("hello", defaultPermissionTier: 0);

            Assert.AreEqual(PermissionTier.Owner, await permissionsService.GetCommandRequiredTier(GuildId, command.Object, database));
            Assert.IsFalse((await permissionsService.CheckUserPermissions(RandomUser, GuildId, TierOverrides.None, command.Object, database)).IsAllowed);
        }

        [TestMethod]
        public async Task User_Effective_Tier_Missing_Row_Defaults_To_Everyone()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var permissionsService = new PermissionsService();

            Assert.AreEqual(PermissionTier.Everyone, await permissionsService.GetUserEffectiveTier(RandomUser, GuildId, TierOverrides.None, database));
        }

        [TestMethod]
        public async Task User_Effective_Tier_Uses_Database_Row()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            database.Users.Add(new DatabaseContext.User { GuildId = GuildId, DiscordId = RandomUser, PermissionTier = PermissionTier.Moderator });
            database.SaveChanges();

            var permissionsService = new PermissionsService();
            var overrides = new TierOverrides { GuildOwnerId = GuildOwner, BotOwnerId = BotOwner };

            Assert.AreEqual(PermissionTier.Moderator, await permissionsService.GetUserEffectiveTier(RandomUser, GuildId, overrides, database));
        }

        [TestMethod]
        public async Task User_Tier_In_One_Guild_Has_No_Effect_In_Another_Guild()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            database.Users.Add(new DatabaseContext.User { GuildId = GuildId, DiscordId = RandomUser, PermissionTier = PermissionTier.Moderator });
            database.SaveChanges();

            var permissionsService = new PermissionsService();
            var overrides = new TierOverrides { GuildOwnerId = GuildOwner, BotOwnerId = BotOwner };

            Assert.AreEqual(PermissionTier.Everyone, await permissionsService.GetUserEffectiveTier(RandomUser, OtherGuildId, overrides, database),
                "A tier stored for guild 1 must not apply in guild 2");
        }

        [TestMethod]
        public async Task Command_Required_Tier_In_One_Guild_Has_No_Effect_In_Another_Guild()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            database.Commands.Add(new DatabaseContext.CommandMetaData { GuildId = GuildId, CommandName = "hello", PermissionTier = PermissionTier.Owner });
            database.SaveChanges();

            var permissionsService = new PermissionsService();
            var command = DiscordTestDoubles.CreateMockCommand("hello", defaultPermissionTier: 0);

            Assert.AreEqual(0, await permissionsService.GetCommandRequiredTier(OtherGuildId, command.Object, database),
                "A command tier override stored for guild 1 must not apply in guild 2");
        }

        [TestMethod]
        public async Task Dm_Every_User_Acts_At_Owner_Tier()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var permissionsService = new PermissionsService();
            var overrides = new TierOverrides { BotOwnerId = BotOwner };

            Assert.AreEqual(PermissionTier.Owner, await permissionsService.GetUserEffectiveTier(RandomUser, null, overrides, database));
            Assert.AreEqual(PermissionTier.BotOwner, await permissionsService.GetUserEffectiveTier(BotOwner, null, overrides, database));
        }

        [TestMethod]
        public async Task Dm_Command_Required_Tier_Is_Always_The_BuiltIn_Default()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            database.Commands.Add(new DatabaseContext.CommandMetaData { GuildId = GuildId, CommandName = "hello", PermissionTier = PermissionTier.Owner });
            database.SaveChanges();

            var permissionsService = new PermissionsService();
            var command = DiscordTestDoubles.CreateMockCommand("hello", defaultPermissionTier: 0);

            Assert.AreEqual(0, await permissionsService.GetCommandRequiredTier(null, command.Object, database),
                "Direct messages must use the command's built-in default tier, never a guild override");
        }

        [TestMethod]
        public async Task Dm_Check_Allows_Default_Tier_Commands()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var permissionsService = new PermissionsService();
            var command = DiscordTestDoubles.CreateMockCommand("hello", defaultPermissionTier: PermissionTier.Admin);

            Assert.IsTrue((await permissionsService.CheckUserPermissions(RandomUser, null, TierOverrides.None, command.Object, database)).IsAllowed);
        }

        [TestMethod]
        public async Task Dm_Check_Denies_Command_Whose_Default_Tier_Exceeds_Owner()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var permissionsService = new PermissionsService();
            var command = DiscordTestDoubles.CreateMockCommand("secret", defaultPermissionTier: PermissionTier.BotOwner);

            Assert.IsFalse((await permissionsService.CheckUserPermissions(RandomUser, null, TierOverrides.None, command.Object, database)).IsAllowed);
            Assert.IsTrue((await permissionsService.CheckUserPermissions(BotOwner, null, new TierOverrides { BotOwnerId = BotOwner }, command.Object, database)).IsAllowed);
        }
    }
}
