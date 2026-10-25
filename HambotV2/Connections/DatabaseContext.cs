using Microsoft.EntityFrameworkCore;

namespace HambotV2.Connections
{
    public class DatabaseContext : DbContext, IDatabaseContext
    {
        public DatabaseContext(DbContextOptions<DatabaseContext> options) : base(options) { }

        public DbSet<User> Users { get; set; }

        public DbSet<CommandMetaData> Commands { get; set; }

        public DbSet<GuildSetting> GuildSettings { get; set; }

        public DbSet<Quote> Quotes { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Per-guild scoping (FR-20/FR-24): tiers and settings are keyed per guild.
            // Rows written before this change sit at the sentinel GuildId 0, which matches no
            // real guild id, so legacy rows are inert (see the PerGuildScoping migration).
            modelBuilder.Entity<User>().HasKey(user => new { user.GuildId, user.DiscordId });
            modelBuilder.Entity<CommandMetaData>().HasKey(command => new { command.GuildId, command.CommandName });
            modelBuilder.Entity<GuildSetting>().HasKey(setting => setting.GuildId);
            modelBuilder.Entity<Quote>().HasKey(quote => new { quote.GuildId, quote.Number });
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

            /// <summary>Per-tier cooldown overrides in seconds; null = inherit the global default.</summary>
            public int? CooldownSecondsTier0 { get; set; }
            public int? CooldownSecondsTier1 { get; set; }
            public int? CooldownSecondsTier2 { get; set; }
            public int? CooldownSecondsTier3 { get; set; }

            /// <summary>The guild's cooldown override for the given tier (0–3, the per-guild configurable tiers); null = inherit the global default.</summary>
            public int? GetCooldownOverride(int tier) => tier switch
            {
                0 => CooldownSecondsTier0,
                1 => CooldownSecondsTier1,
                2 => CooldownSecondsTier2,
                3 => CooldownSecondsTier3,
                _ => null
            };

            /// <summary>Writes the guild's cooldown override for the given tier (no effect outside 0–3; tier 4 is global, configuration-only).</summary>
            public void SetCooldownOverride(int tier, int? seconds)
            {
                switch (tier)
                {
                    case 0: CooldownSecondsTier0 = seconds; break;
                    case 1: CooldownSecondsTier1 = seconds; break;
                    case 2: CooldownSecondsTier2 = seconds; break;
                    case 3: CooldownSecondsTier3 = seconds; break;
                }
            }
        }

        /// <summary>A stored quote, scoped per guild (FR-42); <see cref="Quote.Number"/> is the per-guild sequential index (FR-43).</summary>
        public class Quote
        {
            public ulong GuildId { get; set; }

            /// <summary>Per-guild sequential number: MAX(Number) + 1 at creation (FR-43); never renumbered on removal.</summary>
            public int Number { get; set; }

            public string Text { get; set; } = string.Empty;

            /// <summary>Discord ID of the user who added the quote (FR-42).</summary>
            public ulong AddedByDiscordId { get; set; }

            /// <summary>Timestamp of the adding message (FR-42).</summary>
            public DateTime CreatedAt { get; set; }
        }
    }
}
