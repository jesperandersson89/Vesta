using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VestaServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChannelAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "visibility",
                table: "channels",
                type: "text",
                nullable: false,
                defaultValue: "public");

            migrationBuilder.CreateTable(
                name: "channel_access",
                columns: table => new
                {
                    channel_id = table.Column<string>(type: "text", nullable: false),
                    client_id = table.Column<string>(type: "text", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false, defaultValue: "member"),
                    granted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_channel_access", x => new { x.channel_id, x.client_id });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "channel_access");

            migrationBuilder.DropColumn(
                name: "visibility",
                table: "channels");
        }
    }
}
