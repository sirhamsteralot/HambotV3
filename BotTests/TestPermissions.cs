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

        [TestMethod]
        public void Check_Allowed_Permission_Equal()
        {
            var permissionsService = new PermissionsService();

            Assert.IsTrue(permissionsService.CheckPermissions(0, 0));
        }

        [TestMethod]
        public void Check_Allowed_Permission_Higher()
        {
            var permissionsService = new PermissionsService();

            Assert.IsTrue(permissionsService.CheckPermissions(1, 0));
        }

        [TestMethod]
        public void Check_Disallowed_Permission_Lower()
        {
            var permissionsService = new PermissionsService();

            Assert.IsFalse(permissionsService.CheckPermissions(0, 1));
        }

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
            // Roadmap T16: permission commands carry a built-in default required tier of admin (FR-35),
            // so on a fresh guild (no database rows) a random user is denied.
            var database = DiscordTestDoubles.CreateDatabase();
            var permissionsService = new PermissionsService();
            var userPermission = DiscordTestDoubles.CreateMockCommand("userpermission", defaultPermissionTier: PermissionTier.Admin);

            Assert.IsFalse(await permissionsService.CheckUserPermissions(RandomUser, TierOverrides.None, userPermission.Object, database));
        }

        [TestMethod]
        public async Task FreshGuild_GuildOwner_Can_Run_Permission_Management_Commands()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var permissionsService = new PermissionsService();
            var userPermission = DiscordTestDoubles.CreateMockCommand("userpermission", defaultPermissionTier: PermissionTier.Admin);

            Assert.IsTrue(await permissionsService.CheckUserPermissions(GuildOwner, new TierOverrides { GuildOwnerId = GuildOwner }, userPermission.Object, database));
        }

        [TestMethod]
        public async Task FreshGuild_BotOwner_Can_Run_Permission_Management_Commands()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var permissionsService = new PermissionsService();
            var userPermission = DiscordTestDoubles.CreateMockCommand("userpermission", defaultPermissionTier: PermissionTier.Admin);

            Assert.IsTrue(await permissionsService.CheckUserPermissions(BotOwner, new TierOverrides { BotOwnerId = BotOwner }, userPermission.Object, database));
        }

        [TestMethod]
        public async Task Command_Required_Tier_Missing_Row_Falls_Back_To_Command_Default()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var permissionsService = new PermissionsService();
            var command = DiscordTestDoubles.CreateMockCommand("hello", defaultPermissionTier: 0);

            Assert.AreEqual(0, await permissionsService.GetCommandRequiredTier(command.Object, database));
        }

        [TestMethod]
        public async Task Command_Required_Tier_Database_Override_Wins_Over_Default()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            database.Commands.Add(new DatabaseContext.CommandMetaData { CommandName = "hello", PermissionTier = PermissionTier.Owner });
            database.SaveChanges();

            var permissionsService = new PermissionsService();
            var command = DiscordTestDoubles.CreateMockCommand("hello", defaultPermissionTier: 0);

            Assert.AreEqual(PermissionTier.Owner, await permissionsService.GetCommandRequiredTier(command.Object, database));
            Assert.IsFalse(await permissionsService.CheckUserPermissions(RandomUser, TierOverrides.None, command.Object, database));
        }

        [TestMethod]
        public async Task User_Effective_Tier_Missing_Row_Defaults_To_Everyone()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            var permissionsService = new PermissionsService();

            Assert.AreEqual(PermissionTier.Everyone, await permissionsService.GetUserEffectiveTier(RandomUser, TierOverrides.None, database));
        }

        [TestMethod]
        public async Task User_Effective_Tier_Uses_Database_Row()
        {
            var database = DiscordTestDoubles.CreateDatabase();
            database.Users.Add(new DatabaseContext.User { DiscordId = RandomUser, PermissionTier = PermissionTier.Moderator });
            database.SaveChanges();

            var permissionsService = new PermissionsService();
            var overrides = new TierOverrides { GuildOwnerId = GuildOwner, BotOwnerId = BotOwner };

            Assert.AreEqual(PermissionTier.Moderator, await permissionsService.GetUserEffectiveTier(RandomUser, overrides, database));
        }
    }
}
