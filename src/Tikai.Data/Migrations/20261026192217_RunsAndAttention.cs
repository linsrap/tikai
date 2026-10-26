using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tikai.Data.Migrations
{
    /// <inheritdoc />
    public partial class RunsAndAttention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    progress_step = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    progress_note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    progress_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_runs", x => x.id);
                    table.CheckConstraint("ck_agent_runs_state", "state IN ('queued', 'preparing', 'running', 'blocked', 'in_review', 'changes_requested', 'merged', 'failed', 'cancelled')");
                    table.CheckConstraint("ck_agent_runs_token_hash", "octet_length(token_hash) = 32");
                    table.ForeignKey(
                        name: "fk_agent_runs_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "attention_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    answer = table.Column<string>(type: "jsonb", nullable: true),
                    answered_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    answered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attention_items", x => x.id);
                    table.CheckConstraint("ck_attention_items_answered", "(status = 'answered') = (answer IS NOT NULL AND answered_at IS NOT NULL)");
                    table.CheckConstraint("ck_attention_items_kind", "kind IN ('question', 'permission', 'plan_approval', 'review', 'conflict', 'failure')");
                    table.CheckConstraint("ck_attention_items_status", "status IN ('open', 'answered', 'dismissed')");
                    table.ForeignKey(
                        name: "fk_attention_items_agent_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "agent_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_attention_items_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_attention_items_users_answered_by_id",
                        column: x => x.answered_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_runs_ticket_id",
                table: "agent_runs",
                column: "ticket_id");

            migrationBuilder.CreateIndex(
                name: "ix_agent_runs_token_hash",
                table: "agent_runs",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_attention_items_answered_by_id",
                table: "attention_items",
                column: "answered_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_attention_items_created_at",
                table: "attention_items",
                column: "created_at",
                filter: "status = 'open'");

            migrationBuilder.CreateIndex(
                name: "ix_attention_items_run_id_created_at",
                table: "attention_items",
                columns: new[] { "run_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_attention_items_ticket_id",
                table: "attention_items",
                column: "ticket_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "attention_items");

            migrationBuilder.DropTable(
                name: "agent_runs");
        }
    }
}
