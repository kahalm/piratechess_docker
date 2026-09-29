using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PirateChess.Api.Migrations
{
    /// <inheritdoc />
    public partial class CachedRawLineBid : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Bid",
                table: "CachedRawLines",
                type: "varchar(12)",
                maxLength: 12,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<bool>(
                name: "FromBrowser",
                table: "CachedRawLines",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Bid",
                table: "CachedRawLines");

            migrationBuilder.DropColumn(
                name: "FromBrowser",
                table: "CachedRawLines");
        }
    }
}
