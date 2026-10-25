using Discord;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HambotV2.Connections
{
    public class DatabaseContext : DbContext, IDatabaseContext
    {
        public DatabaseContext(DbContextOptions<DatabaseContext> options) : base(options) { }

        public DbSet<CommandMetaData> Commands { get; set; }

        public DbSet<User> Users { get; set; }

        public DbSet<GuildSetting> GuildSettings { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Per-guild scoping (roadmap T14, FR-20/FR-24): tiers and settings are keyed per guild.
            // Rows written before this change sit at the sentinel GuildId 0, which matches no real
            // guild id, so legacy rows are inert (see the PerGuildScoping migration).
            modelBuilder.Entity<User>().HasKey(user => new { user.GuildId, user.DiscordId });
            modelBuilder.Entity<CommandMetaData>().HasKey(command => new { command.GuildId, command.CommandName });
            modelBuilder.Entity<GuildSetting>().HasKey(setting => setting.GuildId);
        }

        public class User
        {
            public ulong GuildId { get; set; }
            public ulong DiscordId { get; set; }
            public int PermissionTier { get; set; }
        }

        public class CommandMetaData
        {
            public ulong GuildId { get; set; }
            public string CommandName { get; set; } = string.Empty;
            public int PermissionTier { get; set; }
        }

        public class GuildSetting
        {
            public ulong GuildId { get; set; }

            /// <summary>The guild's command identifier override; null means the global default applies (FR-26).</summary>
            public string? CommandIdentifier { get; set; }

            /// <summary>Per-tier cooldown overrides in seconds (roadmap T13 stage 2); null = inherit the global default.</summary>
            public int? CooldownSecondsTier0 { get; set; }
            public int? CooldownSecondsTier1 { get; set; }
            public int? CooldownSecondsTier2 { get; set; }
            public int? CooldownSecondsTier3 { get; set; }
        }
    }
}
