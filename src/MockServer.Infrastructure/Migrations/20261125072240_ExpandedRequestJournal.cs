using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MockServer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ExpandedRequestJournal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DetailsJson",
                table: "SessionRequests",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QueryString",
                table: "SessionRequests",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DetailsJson",
                table: "SessionRequests");

            migrationBuilder.DropColumn(
                name: "QueryString",
                table: "SessionRequests");
        }
    }
}
