using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NzbWebDAV.Database.PostgresMigrations
{
    /// <inheritdoc />
    public partial class AddViewerProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_catalog_watch_history_ResumeKey",
                table: "catalog_watch_history");

            migrationBuilder.DropIndex(
                name: "IX_catalog_watch_history_UpdatedAtEpoch",
                table: "catalog_watch_history");

            migrationBuilder.CreateTable(
                name: "viewer_profiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAtEpoch = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_viewer_profiles", x => x.Id);
                });

            // Seed the profile every existing row is assigned to below. The id is left to the
            // provider so ViewerProfileService.DefaultProfileId (1) matches on a fresh install.
            migrationBuilder.Sql("""
                INSERT INTO "viewer_profiles" ("Name", "CreatedAtEpoch") VALUES ('Main', 0);
                """);

            migrationBuilder.AddColumn<int>(
                name: "ProfileId",
                table: "catalog_watch_history",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("""
                UPDATE "catalog_watch_history"
                SET "ProfileId" = (SELECT MIN("Id") FROM "viewer_profiles");
                """);

            migrationBuilder.CreateIndex(
                name: "IX_catalog_watch_history_ProfileId_UpdatedAtEpoch",
                table: "catalog_watch_history",
                columns: new[] { "ProfileId", "UpdatedAtEpoch" });

            migrationBuilder.CreateIndex(
                name: "IX_catalog_watch_history_ResumeKey",
                table: "catalog_watch_history",
                columns: new[] { "ProfileId", "Kind", "TmdbId", "Season", "Episode" },
                unique: true,
                filter: "NOT \"Finished\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Downgrading collapses every profile back to one identity, so rows owned by any
            // other profile are deleted first: the old resume index is unique over
            // (Kind, TmdbId, Season, Episode) and would reject the duplicate rows that remain.
            migrationBuilder.Sql("""
                DELETE FROM "catalog_watch_history"
                WHERE "ProfileId" <> (SELECT MIN("Id") FROM "viewer_profiles");
                """);

            migrationBuilder.DropTable(
                name: "viewer_profiles");

            migrationBuilder.DropIndex(
                name: "IX_catalog_watch_history_ProfileId_UpdatedAtEpoch",
                table: "catalog_watch_history");

            migrationBuilder.DropIndex(
                name: "IX_catalog_watch_history_ResumeKey",
                table: "catalog_watch_history");

            migrationBuilder.DropColumn(
                name: "ProfileId",
                table: "catalog_watch_history");

            migrationBuilder.CreateIndex(
                name: "IX_catalog_watch_history_ResumeKey",
                table: "catalog_watch_history",
                columns: new[] { "Kind", "TmdbId", "Season", "Episode" },
                unique: true,
                filter: "NOT \"Finished\"");

            migrationBuilder.CreateIndex(
                name: "IX_catalog_watch_history_UpdatedAtEpoch",
                table: "catalog_watch_history",
                column: "UpdatedAtEpoch");
        }
    }
}
