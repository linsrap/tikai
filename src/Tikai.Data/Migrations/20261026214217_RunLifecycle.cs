using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tikai.Data.Migrations
{
    /// <inheritdoc />
    public partial class RunLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "adapter",
                table: "agent_runs",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "branch",
                table: "agent_runs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "cancel_requested_at",
                table: "agent_runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "container_id",
                table: "agent_runs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "cost_tokens",
                table: "agent_runs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "cost_usd",
                table: "agent_runs",
                type: "numeric(12,6)",
                precision: 12,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ended_at",
                table: "agent_runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "exit_code",
                table: "agent_runs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "failure_reason",
                table: "agent_runs",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "last_event_seq",
                table: "agent_runs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "llm_provider_id",
                table: "agent_runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "log_lines_read",
                table: "agent_runs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "model",
                table: "agent_runs",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "repository_id",
                table: "agent_runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "session_id",
                table: "agent_runs",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "started_at",
                table: "agent_runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "started_by_id",
                table: "agent_runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "run_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_run_events", x => x.id);
                    table.CheckConstraint("ck_run_events_kind", "kind IN ('system', 'message', 'tool_call', 'tool_result', 'progress', 'result', 'log')");
                    table.CheckConstraint("ck_run_events_seq", "seq > 0");
                    table.ForeignKey(
                        name: "fk_run_events_agent_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "agent_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_runs_llm_provider_id",
                table: "agent_runs",
                column: "llm_provider_id");

            migrationBuilder.CreateIndex(
                name: "ix_agent_runs_repository_id",
                table: "agent_runs",
                column: "repository_id");

            migrationBuilder.CreateIndex(
                name: "ix_agent_runs_started_by_id",
                table: "agent_runs",
                column: "started_by_id");

            migrationBuilder.CreateIndex(
                name: "ux_agent_runs_active_repository",
                table: "agent_runs",
                column: "repository_id",
                unique: true,
                filter: "state NOT IN ('merged', 'failed', 'cancelled')");

            migrationBuilder.CreateIndex(
                name: "ux_agent_runs_active_ticket",
                table: "agent_runs",
                column: "ticket_id",
                unique: true,
                filter: "state NOT IN ('merged', 'failed', 'cancelled')");

            migrationBuilder.CreateIndex(
                name: "ix_run_events_run_id_seq",
                table: "run_events",
                columns: new[] { "run_id", "seq" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_agent_runs_llm_providers_llm_provider_id",
                table: "agent_runs",
                column: "llm_provider_id",
                principalTable: "llm_providers",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_agent_runs_repositories_repository_id",
                table: "agent_runs",
                column: "repository_id",
                principalTable: "repositories",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_agent_runs_users_started_by_id",
                table: "agent_runs",
                column: "started_by_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_agent_runs_llm_providers_llm_provider_id",
                table: "agent_runs");

            migrationBuilder.DropForeignKey(
                name: "fk_agent_runs_repositories_repository_id",
                table: "agent_runs");

            migrationBuilder.DropForeignKey(
                name: "fk_agent_runs_users_started_by_id",
                table: "agent_runs");

            migrationBuilder.DropTable(
                name: "run_events");

            migrationBuilder.DropIndex(
                name: "ix_agent_runs_llm_provider_id",
                table: "agent_runs");

            migrationBuilder.DropIndex(
                name: "ix_agent_runs_repository_id",
                table: "agent_runs");

            migrationBuilder.DropIndex(
                name: "ix_agent_runs_started_by_id",
                table: "agent_runs");

            migrationBuilder.DropIndex(
                name: "ux_agent_runs_active_repository",
                table: "agent_runs");

            migrationBuilder.DropIndex(
                name: "ux_agent_runs_active_ticket",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "adapter",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "branch",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "cancel_requested_at",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "container_id",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "cost_tokens",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "cost_usd",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "ended_at",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "exit_code",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "failure_reason",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "last_event_seq",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "llm_provider_id",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "log_lines_read",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "model",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "repository_id",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "session_id",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "started_at",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "started_by_id",
                table: "agent_runs");
        }
    }
}
