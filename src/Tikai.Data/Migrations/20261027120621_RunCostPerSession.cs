using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tikai.Data.Migrations
{
    /// <inheritdoc />
    public partial class RunCostPerSession : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "cost_usd_before_session",
                table: "agent_runs",
                type: "numeric(12,6)",
                precision: 12,
                scale: 6,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cost_usd_before_session",
                table: "agent_runs");
        }
    }
}
