using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CtxUi.Builder.Data.Migrations
{
    /// <inheritdoc />
    public partial class InboundIdGuid : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE Servers SET InboundId = upper(InboundId) WHERE InboundId IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE Servers SET InboundId = lower(InboundId) WHERE InboundId IS NOT NULL");
        }
    }
}
