using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace VestaServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminAuditAlertsAppSoftDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deleted_at",
                table: "apps",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "admin_audit",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    admin_public_key = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    target = table.Column<string>(type: "text", nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_audit", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "app_alerts",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    app_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    metric = table.Column<string>(type: "text", nullable: false),
                    threshold_pct = table.Column<int>(type: "integer", nullable: false),
                    observed = table.Column<long>(type: "bigint", nullable: false),
                    limit_value = table.Column<long>(type: "bigint", nullable: false),
                    period_start = table.Column<DateOnly>(type: "date", nullable: true),
                    raised_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    acknowledged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_alerts", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_apps_deleted_at",
                table: "apps",
                column: "deleted_at");

            migrationBuilder.CreateIndex(
                name: "IX_admin_audit_at",
                table: "admin_audit",
                column: "at");

            migrationBuilder.CreateIndex(
                name: "IX_admin_audit_target",
                table: "admin_audit",
                column: "target");

            migrationBuilder.CreateIndex(
                name: "IX_app_alerts_app_id",
                table: "app_alerts",
                column: "app_id");

            migrationBuilder.CreateIndex(
                name: "UX_app_alerts_open",
                table: "app_alerts",
                columns: new[] { "app_id", "metric", "threshold_pct" },
                unique: true,
                filter: "resolved_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admin_audit");

            migrationBuilder.DropTable(
                name: "app_alerts");

            migrationBuilder.DropIndex(
                name: "IX_apps_deleted_at",
                table: "apps");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "apps");
        }
    }
}
