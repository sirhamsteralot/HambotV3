using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HambotV2.Migrations
{
    /// <inheritdoc />
    public partial class AddLinkFix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "LinkFixEnabled",
                table: "GuildSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "LinkFixMappings",
                columns: table => new
                {
                    OriginHost = table.Column<string>(type: "text", nullable: false),
                    MirrorHost = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkFixMappings", x => x.OriginHost);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LinkFixMappings");

            migrationBuilder.DropColumn(
                name: "LinkFixEnabled",
                table: "GuildSettings");
        }
    }
}
