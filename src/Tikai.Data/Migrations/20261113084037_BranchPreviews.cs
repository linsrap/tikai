using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tikai.Data.Migrations
{
    /// <inheritdoc />
    public partial class BranchPreviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "round_no",
                table: "previews",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<Guid>(
                name: "change_set_id",
                table: "previews",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "branch",
                table: "previews",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            // Every preview so far is a round's: its repository is its change set's.
            migrationBuilder.AddColumn<Guid>(
                name: "repository_id",
                table: "previews",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql("UPDATE previews p SET repository_id = c.repository_id FROM change_sets c WHERE c.id = p.change_set_id");

            migrationBuilder.AlterColumn<Guid>(
                name: "repository_id",
                table: "previews",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_previews_repository_id",
                table: "previews",
                column: "repository_id");

            migrationBuilder.CreateIndex(
                name: "ux_previews_active_branch",
                table: "previews",
                column: "repository_id",
                unique: true,
                filter: "branch IS NOT NULL AND state IN ('preparing', 'starting', 'running')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_previews_target",
                table: "previews",
                sql: "(change_set_id IS NOT NULL AND round_no IS NOT NULL AND branch IS NULL) OR (change_set_id IS NULL AND round_no IS NULL AND branch IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "fk_previews_repositories_repository_id",
                table: "previews",
                column: "repository_id",
                principalTable: "repositories",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Branch previews have no change set to go back to (their log lines and entries cascade).
            migrationBuilder.Sql("DELETE FROM previews WHERE branch IS NOT NULL");

            migrationBuilder.DropForeignKey(
                name: "fk_previews_repositories_repository_id",
                table: "previews");

            migrationBuilder.DropIndex(
                name: "ix_previews_repository_id",
                table: "previews");

            migrationBuilder.DropIndex(
                name: "ux_previews_active_branch",
                table: "previews");

            migrationBuilder.DropCheckConstraint(
                name: "ck_previews_target",
                table: "previews");

            migrationBuilder.DropColumn(
                name: "branch",
                table: "previews");

            migrationBuilder.DropColumn(
                name: "repository_id",
                table: "previews");

            migrationBuilder.AlterColumn<int>(
                name: "round_no",
                table: "previews",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "change_set_id",
                table: "previews",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
