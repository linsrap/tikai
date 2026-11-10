using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tikai.Data.Migrations
{
    /// <inheritdoc />
    public partial class Previews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "previews",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    change_set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    round_no = table.Column<int>(type: "integer", nullable: false),
                    head_sha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    environment = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    stop_reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    definition = table.Column<string>(type: "text", nullable: false),
                    idle_minutes = table.Column<int>(type: "integer", nullable: false),
                    started_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    running_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_request_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    stopped_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cleaned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    upstream = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    app_container_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    last_log_seq = table.Column<long>(type: "bigint", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_previews", x => x.id);
                    table.CheckConstraint("ck_previews_ended", "(state IN ('stopped', 'failed')) = (stopped_at IS NOT NULL)");
                    table.CheckConstraint("ck_previews_failed", "(state = 'failed') = (error IS NOT NULL)");
                    table.CheckConstraint("ck_previews_round_no", "round_no > 0");
                    table.CheckConstraint("ck_previews_state", "state IN ('preparing', 'starting', 'running', 'stopped', 'failed')");
                    table.ForeignKey(
                        name: "fk_previews_change_sets_change_set_id",
                        column: x => x.change_set_id,
                        principalTable: "change_sets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_previews_users_started_by_id",
                        column: x => x.started_by_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "preview_log_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    preview_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<long>(type: "bigint", nullable: false),
                    source = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_preview_log_lines", x => x.id);
                    table.ForeignKey(
                        name: "fk_preview_log_lines_previews_preview_id",
                        column: x => x.preview_id,
                        principalTable: "previews",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_preview_log_lines_preview_id_seq",
                table: "preview_log_lines",
                columns: new[] { "preview_id", "seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_previews_change_set_id",
                table: "previews",
                column: "change_set_id");

            migrationBuilder.CreateIndex(
                name: "ix_previews_started_by_id",
                table: "previews",
                column: "started_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_previews_state",
                table: "previews",
                column: "state");

            migrationBuilder.CreateIndex(
                name: "ux_previews_active_change_set",
                table: "previews",
                column: "change_set_id",
                unique: true,
                filter: "state IN ('preparing', 'starting', 'running')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "preview_log_lines");

            migrationBuilder.DropTable(
                name: "previews");
        }
    }
}
