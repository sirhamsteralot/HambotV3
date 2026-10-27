using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HambotV2.Migrations
{
    /// <inheritdoc />
    public partial class AddQuoteOfTheDay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "QuoteOfTheDayAnchorUtc",
                table: "GuildSettings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "QuoteOfTheDayChannelId",
                table: "GuildSettings",
                type: "numeric(20,0)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QuoteOfTheDayIntervalMinutes",
                table: "GuildSettings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QuoteOfTheDayTemplate",
                table: "GuildSettings",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QuoteOfTheDayAnchorUtc",
                table: "GuildSettings");

            migrationBuilder.DropColumn(
                name: "QuoteOfTheDayChannelId",
                table: "GuildSettings");

            migrationBuilder.DropColumn(
                name: "QuoteOfTheDayIntervalMinutes",
                table: "GuildSettings");

            migrationBuilder.DropColumn(
                name: "QuoteOfTheDayTemplate",
                table: "GuildSettings");
        }
    }
}
