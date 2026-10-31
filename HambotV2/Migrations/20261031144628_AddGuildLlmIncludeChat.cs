using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HambotV2.Migrations
{
    /// <inheritdoc />
    public partial class AddGuildLlmIncludeChat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "LlmIncludeChat",
                table: "GuildSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LlmIncludeChat",
                table: "GuildSettings");
        }
    }
}
