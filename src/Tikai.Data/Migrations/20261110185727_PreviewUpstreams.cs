using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tikai.Data.Migrations
{
    /// <inheritdoc />
    public partial class PreviewUpstreams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "upstreams",
                table: "previews",
                type: "jsonb",
                nullable: true);

            // A running preview keeps its one app (named app, on /).
            migrationBuilder.Sql("""
                UPDATE previews
                SET upstreams = jsonb_build_array(jsonb_build_object('name', 'app', 'path', '/', 'url', upstream, 'container_id', app_container_id))
                WHERE upstream IS NOT NULL AND app_container_id IS NOT NULL;
                """);

            migrationBuilder.DropColumn(
                name: "app_container_id",
                table: "previews");

            migrationBuilder.DropColumn(
                name: "upstream",
                table: "previews");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "app_container_id",
                table: "previews",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "upstream",
                table: "previews",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE previews p
                SET upstream = a->>'url', app_container_id = a->>'container_id'
                FROM previews q CROSS JOIN LATERAL jsonb_array_elements(q.upstreams) a
                WHERE q.id = p.id AND a->>'path' = '/';
                """);

            migrationBuilder.DropColumn(
                name: "upstreams",
                table: "previews");
        }
    }
}
