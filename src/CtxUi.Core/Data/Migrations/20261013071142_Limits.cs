using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CtxUi.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class Limits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxBodyBytes",
                table: "Inbounds",
                type: "INTEGER",
                nullable: false,
                defaultValue: 4194304);

            migrationBuilder.AddColumn<int>(
                name: "MaxConcurrency",
                table: "Clients",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PeriodStartedAt",
                table: "Clients",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "QuotaCalls",
                table: "Clients",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RatePerMinute",
                table: "Clients",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResetEveryDays",
                table: "Clients",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "UsedCalls",
                table: "Clients",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "UsedSince",
                table: "Clients",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "UsageDays",
                columns: table => new
                {
                    Day = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    ClientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InboundId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Calls = table.Column<int>(type: "INTEGER", nullable: false),
                    ToolCalls = table.Column<int>(type: "INTEGER", nullable: false),
                    Errors = table.Column<int>(type: "INTEGER", nullable: false),
                    Refused = table.Column<int>(type: "INTEGER", nullable: false),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageDays", x => new { x.Day, x.ClientId, x.InboundId });
                });

            migrationBuilder.CreateTable(
                name: "UsageEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ClientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InboundId = table.Column<Guid>(type: "TEXT", nullable: false),
                    At = table.Column<long>(type: "INTEGER", nullable: false),
                    Method = table.Column<string>(type: "TEXT", nullable: false),
                    Tool = table.Column<string>(type: "TEXT", nullable: true),
                    DurationMs = table.Column<int>(type: "INTEGER", nullable: false),
                    Outcome = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UsageEvents_At",
                table: "UsageEvents",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_UsageEvents_ClientId_At",
                table: "UsageEvents",
                columns: new[] { "ClientId", "At" });

            migrationBuilder.Sql("UPDATE Clients SET PeriodStartedAt = CreatedAt;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UsageDays");

            migrationBuilder.DropTable(
                name: "UsageEvents");

            migrationBuilder.DropColumn(
                name: "MaxBodyBytes",
                table: "Inbounds");

            migrationBuilder.DropColumn(
                name: "MaxConcurrency",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "PeriodStartedAt",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "QuotaCalls",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "RatePerMinute",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "ResetEveryDays",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "UsedCalls",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "UsedSince",
                table: "Clients");
        }
    }
}
