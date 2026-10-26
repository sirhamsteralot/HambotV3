using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HambotV2.Migrations
{
    /// <inheritdoc />
    public partial class AddOperatorAgentTier : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OperatorAgentTier",
                table: "GuildSettings",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OperatorAgentTier",
                table: "GuildSettings");
        }
    }
}
