using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tikai.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReviewAnalysis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "analysis",
                table: "review_rounds",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "analysis_at",
                table: "review_rounds",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "analysis_error",
                table: "review_rounds",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "analysis_status",
                table: "review_rounds",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "settings",
                table: "projects",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");

            migrationBuilder.AddCheckConstraint(
                name: "ck_review_rounds_analysis_done",
                table: "review_rounds",
                sql: "(analysis_status = 'done') = (analysis IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_review_rounds_analysis_status",
                table: "review_rounds",
                sql: "analysis_status IN ('pending', 'done', 'failed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_review_rounds_analysis_done",
                table: "review_rounds");

            migrationBuilder.DropCheckConstraint(
                name: "ck_review_rounds_analysis_status",
                table: "review_rounds");

            migrationBuilder.DropColumn(
                name: "analysis",
                table: "review_rounds");

            migrationBuilder.DropColumn(
                name: "analysis_at",
                table: "review_rounds");

            migrationBuilder.DropColumn(
                name: "analysis_error",
                table: "review_rounds");

            migrationBuilder.DropColumn(
                name: "analysis_status",
                table: "review_rounds");

            migrationBuilder.DropColumn(
                name: "settings",
                table: "projects");
        }
    }
}
