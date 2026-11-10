using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tikai.Data.Migrations
{
    /// <inheritdoc />
    public partial class PreviewEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "preview_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    preview_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_preview_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_preview_entries_previews_preview_id",
                        column: x => x.preview_id,
                        principalTable: "previews",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_preview_entries_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_preview_entries_preview_id",
                table: "preview_entries",
                column: "preview_id");

            migrationBuilder.CreateIndex(
                name: "ix_preview_entries_token_hash",
                table: "preview_entries",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_preview_entries_user_id",
                table: "preview_entries",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "preview_entries");
        }
    }
}
