using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HambotV2.Migrations
{
    /// <inheritdoc />
    public partial class AddBotBans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BotBans",
                columns: table => new
                {
                    GuildId = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    DiscordId = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    BannedByDiscordId = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BotBans", x => new { x.GuildId, x.DiscordId });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BotBans");
        }
    }
}
