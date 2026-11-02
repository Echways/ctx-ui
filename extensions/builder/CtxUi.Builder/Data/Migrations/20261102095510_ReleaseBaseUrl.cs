using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CtxUi.Builder.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReleaseBaseUrl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BaseUrl",
                table: "Releases",
                type: "TEXT",
                nullable: true);

            migrationBuilder.Sql("UPDATE Releases SET BaseUrl = (SELECT BaseUrl FROM Drafts WHERE Drafts.ServerId = Releases.ServerId)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BaseUrl",
                table: "Releases");
        }
    }
}
