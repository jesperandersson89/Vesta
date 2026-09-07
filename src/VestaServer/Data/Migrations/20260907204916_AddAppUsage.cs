using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VestaServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAppUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "app_usage",
                columns: table => new
                {
                    app_id = table.Column<string>(type: "text", nullable: false),
                    period_start = table.Column<DateOnly>(type: "date", nullable: false),
                    messages = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_usage", x => new { x.app_id, x.period_start });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_usage");
        }
    }
}
