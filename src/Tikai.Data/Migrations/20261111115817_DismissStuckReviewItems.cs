using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tikai.Data.Migrations
{
    /// <inheritdoc />
    public partial class DismissStuckReviewItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A run that ended used to leave its review item open (only a verdict closed it), and such an item could
            // not be closed in the inbox. Ending a run dismisses them now; this closes the ones already stuck.
            migrationBuilder.Sql("""
                UPDATE attention_items i
                SET status = 'dismissed', updated_at = now()
                FROM agent_runs r
                WHERE r.id = i.run_id
                  AND i.status = 'open'
                  AND i.kind IN ('review', 'conflict')
                  AND r.state IN ('merged', 'pushed', 'failed', 'cancelled');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The dismissed items stay dismissed: nothing tells them from the ones people dismissed.
        }
    }
}
