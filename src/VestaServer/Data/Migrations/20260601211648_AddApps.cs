using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VestaServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddApps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "apps",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    owner_client_id = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    max_channels = table.Column<int>(type: "integer", nullable: true),
                    max_events_per_channel = table.Column<int>(type: "integer", nullable: true),
                    max_payload_bytes = table.Column<int>(type: "integer", nullable: true),
                    publish_rate_per_minute = table.Column<int>(type: "integer", nullable: true),
                    retention_days = table.Column<int>(type: "integer", nullable: true),
                    total_storage_bytes = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_apps", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_apps_owner_client_id",
                table: "apps",
                column: "owner_client_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "apps");
        }
    }
}
