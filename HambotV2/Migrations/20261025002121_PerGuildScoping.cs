using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HambotV2.Migrations
{
    /// <inheritdoc />
    public partial class PerGuildScoping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Roadmap T14 (FR-20/FR-24): adds the per-guild dimension to permission state. Existing
            // rows keep the sentinel GuildId 0 (the column default), which matches no real guild id,
            // so legacy tiers become inert; admins re-grant tiers per guild. Guild owners already
            // bootstrap at owner tier (FR-34), so fresh guilds need no data.
            migrationBuilder.DropPrimaryKey(
                name: "PK_Users",
                table: "Users");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Commands",
                table: "Commands");

            migrationBuilder.AddColumn<decimal>(
                name: "GuildId",
                table: "Users",
                type: "numeric(20,0)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "GuildId",
                table: "Commands",
                type: "numeric(20,0)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddPrimaryKey(
                name: "PK_Users",
                table: "Users",
                columns: new[] { "GuildId", "DiscordId" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_Commands",
                table: "Commands",
                columns: new[] { "GuildId", "CommandName" });

            migrationBuilder.CreateTable(
                name: "GuildSettings",
                columns: table => new
                {
                    GuildId = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    CommandIdentifier = table.Column<string>(type: "text", nullable: true),
                    CooldownSecondsTier0 = table.Column<int>(type: "integer", nullable: true),
                    CooldownSecondsTier1 = table.Column<int>(type: "integer", nullable: true),
                    CooldownSecondsTier2 = table.Column<int>(type: "integer", nullable: true),
                    CooldownSecondsTier3 = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GuildSettings", x => x.GuildId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GuildSettings");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Users",
                table: "Users");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Commands",
                table: "Commands");

            migrationBuilder.DropColumn(
                name: "GuildId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "GuildId",
                table: "Commands");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Users",
                table: "Users",
                column: "DiscordId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Commands",
                table: "Commands",
                column: "CommandName");
        }
    }
}
