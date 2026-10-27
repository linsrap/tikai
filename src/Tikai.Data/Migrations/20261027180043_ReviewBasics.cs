using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tikai.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReviewBasics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "base_sha",
                table: "agent_runs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "change_sets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repository_id = table.Column<Guid>(type: "uuid", nullable: false),
                    base_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    head_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_change_sets", x => x.id);
                    table.CheckConstraint("ck_change_sets_status", "status IN ('open', 'merged', 'closed')");
                    table.ForeignKey(
                        name: "fk_change_sets_agent_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "agent_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_change_sets_repositories_repository_id",
                        column: x => x.repository_id,
                        principalTable: "repositories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_change_sets_tickets_ticket_id",
                        column: x => x.ticket_id,
                        principalTable: "tickets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "file_view_states",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    change_set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    viewed_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_file_view_states", x => x.id);
                    table.ForeignKey(
                        name: "fk_file_view_states_change_sets_change_set_id",
                        column: x => x.change_set_id,
                        principalTable: "change_sets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_file_view_states_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "review_rounds",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    change_set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    round_no = table.Column<int>(type: "integer", nullable: false),
                    base_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    head_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    self_report = table.Column<string>(type: "jsonb", nullable: false),
                    uncommitted = table.Column<string[]>(type: "text[]", nullable: false),
                    verdict = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    verdict_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    verdict_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_review_rounds", x => x.id);
                    table.CheckConstraint("ck_review_rounds_round_no", "round_no > 0");
                    table.CheckConstraint("ck_review_rounds_verdict", "verdict IN ('approve', 'request_changes', 'reject')");
                    table.CheckConstraint("ck_review_rounds_verdict_given", "(verdict IS NULL) = (verdict_at IS NULL)");
                    table.ForeignKey(
                        name: "fk_review_rounds_change_sets_change_set_id",
                        column: x => x.change_set_id,
                        principalTable: "change_sets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_review_rounds_users_verdict_by_id",
                        column: x => x.verdict_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_change_sets_repository_id",
                table: "change_sets",
                column: "repository_id");

            migrationBuilder.CreateIndex(
                name: "ix_change_sets_run_id",
                table: "change_sets",
                column: "run_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_change_sets_ticket_id",
                table: "change_sets",
                column: "ticket_id");

            migrationBuilder.CreateIndex(
                name: "ix_file_view_states_change_set_id",
                table: "file_view_states",
                column: "change_set_id");

            migrationBuilder.CreateIndex(
                name: "ix_file_view_states_user_id_change_set_id_file_path",
                table: "file_view_states",
                columns: new[] { "user_id", "change_set_id", "file_path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_review_rounds_change_set_id_round_no",
                table: "review_rounds",
                columns: new[] { "change_set_id", "round_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_review_rounds_verdict_by_id",
                table: "review_rounds",
                column: "verdict_by_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "file_view_states");

            migrationBuilder.DropTable(
                name: "review_rounds");

            migrationBuilder.DropTable(
                name: "change_sets");

            migrationBuilder.DropColumn(
                name: "base_sha",
                table: "agent_runs");
        }
    }
}
