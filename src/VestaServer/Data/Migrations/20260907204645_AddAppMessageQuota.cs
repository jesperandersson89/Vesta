using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VestaServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAppMessageQuota : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "max_messages_per_month",
                table: "apps",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "max_messages_per_month",
                table: "apps");
        }
    }
}
