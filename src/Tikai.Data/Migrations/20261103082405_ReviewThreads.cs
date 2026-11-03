using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tikai.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReviewThreads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "review_threads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    change_set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    review_round_id = table.Column<Guid>(type: "uuid", nullable: false),
                    file_path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    side = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    line_start = table.Column<int>(type: "integer", nullable: true),
                    line_end = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_review_threads", x => x.id);
                    table.CheckConstraint("ck_review_threads_anchor", "(file_path IS NULL AND side IS NULL AND line_start IS NULL AND line_end IS NULL) OR (file_path IS NOT NULL AND side IS NOT NULL AND line_start >= 1 AND line_end >= line_start)");
                    table.CheckConstraint("ck_review_threads_resolved", "(status = 'resolved') = (resolved_at IS NOT NULL)");
                    table.CheckConstraint("ck_review_threads_side", "side IN ('old', 'new')");
                    table.CheckConstraint("ck_review_threads_status", "status IN ('open', 'resolved')");
                    table.ForeignKey(
                        name: "fk_review_threads_change_sets_change_set_id",
                        column: x => x.change_set_id,
                        principalTable: "change_sets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_review_threads_review_rounds_review_round_id",
                        column: x => x.review_round_id,
                        principalTable: "review_rounds",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_review_threads_users_created_by_id",
                        column: x => x.created_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_review_threads_users_resolved_by_id",
                        column: x => x.resolved_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "review_comments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    thread_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    author_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    body = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    edited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_review_comments", x => x.id);
                    table.CheckConstraint("ck_review_comments_action", "action IN ('note', 'send_to_agent')");
                    table.CheckConstraint("ck_review_comments_author", "(author_user_id IS NULL) <> (author_run_id IS NULL)");
                    table.CheckConstraint("ck_review_comments_sent", "sent_at IS NULL OR action = 'send_to_agent'");
                    table.ForeignKey(
                        name: "fk_review_comments_agent_runs_author_run_id",
                        column: x => x.author_run_id,
                        principalTable: "agent_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_review_comments_review_threads_thread_id",
                        column: x => x.thread_id,
                        principalTable: "review_threads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_review_comments_users_author_user_id",
                        column: x => x.author_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_review_comments_author_run_id",
                table: "review_comments",
                column: "author_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_review_comments_author_user_id",
                table: "review_comments",
                column: "author_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_review_comments_thread_id",
                table: "review_comments",
                column: "thread_id");

            migrationBuilder.CreateIndex(
                name: "ix_review_threads_change_set_id",
                table: "review_threads",
                column: "change_set_id");

            migrationBuilder.CreateIndex(
                name: "ix_review_threads_created_by_id",
                table: "review_threads",
                column: "created_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_review_threads_resolved_by_id",
                table: "review_threads",
                column: "resolved_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_review_threads_review_round_id",
                table: "review_threads",
                column: "review_round_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "review_comments");

            migrationBuilder.DropTable(
                name: "review_threads");
        }
    }
}
