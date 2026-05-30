using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VestaServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNotifyTrigger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION notify_new_event() RETURNS trigger AS $$
                BEGIN
                    PERFORM pg_notify('vesta_events', json_build_object(
                        'channel_id', NEW.channel_id,
                        'sequence', NEW.sequence,
                        'event_type', NEW.event_type
                    )::text);
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER trg_notify_new_event
                    AFTER INSERT ON events
                    FOR EACH ROW EXECUTE FUNCTION notify_new_event();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_notify_new_event ON events;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS notify_new_event();");
        }
    }
}
