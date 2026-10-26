using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VestaServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAppOwnerOnlyChannelCreation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "owner_only_channel_creation",
                table: "apps",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "owner_only_channel_creation",
                table: "apps");
        }
    }
}
