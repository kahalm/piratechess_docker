using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PirateChess.Api.Migrations
{
    /// <inheritdoc />
    public partial class CachedRawLineInvalid : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "InvalidAt",
                table: "CachedRawLines",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvalidReason",
                table: "CachedRawLines",
                type: "varchar(200)",
                maxLength: 200,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "CachedRawLineArchive",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Oid = table.Column<int>(type: "int", nullable: false),
                    LineJsonContent = table.Column<string>(type: "LONGTEXT", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CachedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    InvalidAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    InvalidReason = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ArchivedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CachedRawLineArchive", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_CachedRawLineArchive_Oid",
                table: "CachedRawLineArchive",
                column: "Oid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CachedRawLineArchive");

            migrationBuilder.DropColumn(
                name: "InvalidAt",
                table: "CachedRawLines");

            migrationBuilder.DropColumn(
                name: "InvalidReason",
                table: "CachedRawLines");
        }
    }
}
