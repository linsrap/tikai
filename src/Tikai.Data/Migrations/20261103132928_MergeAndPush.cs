using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tikai.Data.Migrations
{
    /// <inheritdoc />
    public partial class MergeAndPush : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_change_sets_status",
                table: "change_sets");

            migrationBuilder.DropIndex(
                name: "ux_agent_runs_active_repository",
                table: "agent_runs");

            migrationBuilder.DropIndex(
                name: "ux_agent_runs_active_ticket",
                table: "agent_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_agent_runs_state",
                table: "agent_runs");

            migrationBuilder.AddColumn<bool>(
                name: "rebase_requested",
                table: "review_rounds",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "delivered_sha",
                table: "change_sets",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "delivery_error",
                table: "change_sets",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "cleaned_at",
                table: "agent_runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_change_sets_delivered",
                table: "change_sets",
                sql: "(status IN ('merged', 'pushed')) = (delivered_sha IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_change_sets_status",
                table: "change_sets",
                sql: "status IN ('open', 'delivering', 'merged', 'pushed', 'closed')");

            migrationBuilder.CreateIndex(
                name: "ux_agent_runs_active_repository",
                table: "agent_runs",
                column: "repository_id",
                unique: true,
                filter: "state NOT IN ('merged', 'pushed', 'failed', 'cancelled')");

            migrationBuilder.CreateIndex(
                name: "ux_agent_runs_active_ticket",
                table: "agent_runs",
                column: "ticket_id",
                unique: true,
                filter: "state NOT IN ('merged', 'pushed', 'failed', 'cancelled')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_agent_runs_state",
                table: "agent_runs",
                sql: "state IN ('queued', 'preparing', 'running', 'blocked', 'in_review', 'changes_requested', 'merged', 'pushed', 'failed', 'cancelled')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_change_sets_delivered",
                table: "change_sets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_change_sets_status",
                table: "change_sets");

            migrationBuilder.DropIndex(
                name: "ux_agent_runs_active_repository",
                table: "agent_runs");

            migrationBuilder.DropIndex(
                name: "ux_agent_runs_active_ticket",
                table: "agent_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_agent_runs_state",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "rebase_requested",
                table: "review_rounds");

            migrationBuilder.DropColumn(
                name: "delivered_sha",
                table: "change_sets");

            migrationBuilder.DropColumn(
                name: "delivery_error",
                table: "change_sets");

            migrationBuilder.DropColumn(
                name: "cleaned_at",
                table: "agent_runs");

            migrationBuilder.AddCheckConstraint(
                name: "ck_change_sets_status",
                table: "change_sets",
                sql: "status IN ('open', 'merged', 'closed')");

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

            migrationBuilder.AddCheckConstraint(
                name: "ck_agent_runs_state",
                table: "agent_runs",
                sql: "state IN ('queued', 'preparing', 'running', 'blocked', 'in_review', 'changes_requested', 'merged', 'failed', 'cancelled')");
        }
    }
}
