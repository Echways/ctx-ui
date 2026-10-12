using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CtxUi.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Clients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    NameKey = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", nullable: false),
                    TokenSealed = table.Column<string>(type: "TEXT", nullable: false),
                    TokenPrefix = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: true),
                    FirstUsedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastUsedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Metadata = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Clients", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Inbounds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Slug = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestTimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    Url = table.Column<string>(type: "TEXT", nullable: true),
                    UpstreamHeadersSealed = table.Column<string>(type: "TEXT", nullable: true),
                    ForwardIdentity = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Inbounds", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RoutingStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoutingStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClientInbounds",
                columns: table => new
                {
                    ClientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InboundId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientInbounds", x => new { x.ClientId, x.InboundId });
                    table.ForeignKey(
                        name: "FK_ClientInbounds_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClientInbounds_Inbounds_InboundId",
                        column: x => x.InboundId,
                        principalTable: "Inbounds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "RoutingStates",
                columns: new[] { "Id", "Revision" },
                values: new object[] { 1, 0L });

            migrationBuilder.CreateIndex(
                name: "IX_ClientInbounds_InboundId",
                table: "ClientInbounds",
                column: "InboundId");

            migrationBuilder.CreateIndex(
                name: "IX_Clients_NameKey",
                table: "Clients",
                column: "NameKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Clients_TokenHash",
                table: "Clients",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Inbounds_Slug",
                table: "Inbounds",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientInbounds");

            migrationBuilder.DropTable(
                name: "RoutingStates");

            migrationBuilder.DropTable(
                name: "Clients");

            migrationBuilder.DropTable(
                name: "Inbounds");
        }
    }
}
