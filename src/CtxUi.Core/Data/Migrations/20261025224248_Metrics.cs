using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CtxUi.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class Metrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RequestBytes",
                table: "UsageEvents",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "ResponseBytes",
                table: "UsageEvents",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "UsageEvents",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "UsageRefusals",
                columns: table => new
                {
                    Minute = table.Column<long>(type: "INTEGER", nullable: false),
                    ClientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InboundId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Outcome = table.Column<int>(type: "INTEGER", nullable: false),
                    Count = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageRefusals", x => new { x.Minute, x.ClientId, x.InboundId, x.Outcome });
                });

            migrationBuilder.CreateIndex(
                name: "IX_UsageEvents_InboundId_At",
                table: "UsageEvents",
                columns: new[] { "InboundId", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UsageRefusals");

            migrationBuilder.DropIndex(
                name: "IX_UsageEvents_InboundId_At",
                table: "UsageEvents");

            migrationBuilder.DropColumn(
                name: "RequestBytes",
                table: "UsageEvents");

            migrationBuilder.DropColumn(
                name: "ResponseBytes",
                table: "UsageEvents");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "UsageEvents");
        }
    }
}
