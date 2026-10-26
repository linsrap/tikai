using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tikai.Data.Migrations
{
    /// <inheritdoc />
    public partial class LlmProviders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "llm_providers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    owner_project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    base_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    default_model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    encrypted_api_key = table.Column<string>(type: "text", nullable: false),
                    key_hint = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_llm_providers", x => x.id);
                    table.CheckConstraint("ck_llm_providers_kind", "kind IN ('anthropic')");
                    table.CheckConstraint("ck_llm_providers_one_owner", "(owner_user_id IS NULL) <> (owner_project_id IS NULL)");
                    table.ForeignKey(
                        name: "fk_llm_providers_projects_owner_project_id",
                        column: x => x.owner_project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_llm_providers_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_llm_providers_owner_project_id_name",
                table: "llm_providers",
                columns: new[] { "owner_project_id", "name" },
                unique: true,
                filter: "owner_project_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_llm_providers_owner_user_id_name",
                table: "llm_providers",
                columns: new[] { "owner_user_id", "name" },
                unique: true,
                filter: "owner_user_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "llm_providers");
        }
    }
}
