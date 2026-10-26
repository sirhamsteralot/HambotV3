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

        public DbSet<CustomResponse> CustomResponses { get; set; }

        public DbSet<Reminder> Reminders { get; set; }

        public DbSet<BotSetting> BotSettings { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Per-guild scoping: tiers and settings are keyed per guild.
            // Rows written before this change sit at the sentinel GuildId 0, which matches no
            // real guild id, so legacy rows are inert (see the PerGuildScoping migration).
            modelBuilder.Entity<User>().HasKey(user => new { user.GuildId, user.DiscordId });
            modelBuilder.Entity<CommandMetaData>().HasKey(command => new { command.GuildId, command.CommandName });
            modelBuilder.Entity<GuildSetting>().HasKey(setting => setting.GuildId);
            modelBuilder.Entity<Quote>().HasKey(quote => new { quote.GuildId, quote.Number });
            modelBuilder.Entity<CustomResponse>().HasKey(response => new { response.GuildId, response.Name });
            modelBuilder.Entity<Reminder>().HasKey(reminder => new { reminder.OwnerDiscordId, reminder.Number });
            // Global key-value settings: the global analog of GuildSetting.
            modelBuilder.Entity<BotSetting>().HasKey(setting => setting.Key);
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

            /// <summary>The guild's command identifier override; null means the global default applies.</summary>
            public string? CommandIdentifier { get; set; }

            /// <summary>The tier the MCP operator agent acts at in this guild; null = the lowest-tier default.</summary>
            public int? OperatorAgentTier { get; set; }

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

        /// <summary>A stored quote, scoped per guild; <see cref="Quote.Number"/> is the per-guild sequential index.</summary>
        public class Quote
        {
            public ulong GuildId { get; set; }

            /// <summary>Per-guild sequential number: MAX(Number) + 1 at creation; never renumbered on removal.</summary>
            public int Number { get; set; }

            public string Text { get; set; } = string.Empty;

            /// <summary>Discord ID of the user who added the quote.</summary>
            public ulong AddedByDiscordId { get; set; }

            /// <summary>Timestamp of the adding message.</summary>
            public DateTime CreatedAt { get; set; }
        }

        /// <summary>A user-defined text response, scoped per guild; dispatched when its name matches an otherwise unknown command token.</summary>
        public class CustomResponse
        {
            public ulong GuildId { get; set; }

            /// <summary>The response name: a single lowercase token of 1–32 characters — lowercase letters, digits, hyphen, underscore.</summary>
            public string Name { get; set; } = string.Empty;

            /// <summary>The reply text (1–1000 characters): plain text only — links are stored verbatim, files are never stored.</summary>
            public string Content { get; set; } = string.Empty;

            /// <summary>Discord ID of the user who last modified the response.</summary>
            public ulong LastModifiedByDiscordId { get; set; }

            public DateTime CreatedAt { get; set; }

            public DateTime UpdatedAt { get; set; }
        }

        /// <summary>A pending reminder, scoped per user: the key carries the owner, so reminders follow them across guilds and direct messages.</summary>
        public class Reminder
        {
            /// <summary>Discord ID of the user the reminder belongs to and is delivered to.</summary>
            public ulong OwnerDiscordId { get; set; }

            /// <summary>Per-owner sequential number: MAX(Number) + 1 at creation; never renumbered on cancellation.</summary>
            public int Number { get; set; }

            /// <summary>The reminder text (1–500 characters).</summary>
            public string Message { get; set; } = string.Empty;

            /// <summary>Due time, always UTC.</summary>
            public DateTimeOffset DueAtUtc { get; set; }

            /// <summary>Recurrence interval for recurring reminders; null = one-shot (the pre-existing rows' value).</summary>
            public TimeSpan? RecurrenceInterval { get; set; }

            /// <summary>Guild the reminder was created in; null when created in a direct message.</summary>
            public ulong? GuildId { get; set; }

            public DateTime CreatedAt { get; set; }

            /// <summary>Set when delivery failed after the bounded retries; null = pending. Failed rows stay for audit but leave the pending set.</summary>
            public DateTimeOffset? FailedAtUtc { get; set; }
        }

        /// <summary>
        /// A global key-value setting: bot-owner-scope state that is not guild- or user-scoped,
        /// persisted across restarts. First key: the operator notification channel id.
        /// </summary>
        public class BotSetting
        {
            public string Key { get; set; } = string.Empty;
            public string? Value { get; set; }
        }
    }
}
