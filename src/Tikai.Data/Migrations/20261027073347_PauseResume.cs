using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tikai.Data.Migrations
{
    /// <inheritdoc />
    public partial class PauseResume : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "delivered_at",
                table: "attention_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "attempt",
                table: "agent_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "attempt_kind",
                table: "agent_runs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "start");

            migrationBuilder.AddCheckConstraint(
                name: "ck_agent_runs_attempt_kind",
                table: "agent_runs",
                sql: "attempt_kind IN ('start', 'resume', 'summary')");

            // Runs that already had a container had one attempt. Answers given before this step count as delivered,
            // except those of runs still blocked: their agent gave up waiting, so a resume must bring the answer.
            migrationBuilder.Sql("UPDATE agent_runs SET attempt = 1 WHERE started_at IS NOT NULL;");
            migrationBuilder.Sql("""
                UPDATE attention_items i SET delivered_at = i.answered_at
                WHERE i.status = 'answered'
                  AND NOT EXISTS (SELECT 1 FROM agent_runs r WHERE r.id = i.run_id AND r.state = 'blocked');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_agent_runs_attempt_kind",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "delivered_at",
                table: "attention_items");

            migrationBuilder.DropColumn(
                name: "attempt",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "attempt_kind",
                table: "agent_runs");
        }
    }
}
