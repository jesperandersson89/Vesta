using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VestaServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAppRealtimeQuotas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "max_connections",
                table: "apps",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "volatile_rate_per_minute",
                table: "apps",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "max_connections",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "volatile_rate_per_minute",
                table: "apps");
        }
    }
}
