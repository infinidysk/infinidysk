using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NzbWebDAV.Database.PostgresMigrations
{
    /// <inheritdoc />
    public partial class AddCatalogWatchHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "catalog_watch_history",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    TmdbId = table.Column<int>(type: "integer", nullable: false),
                    Season = table.Column<int>(type: "integer", nullable: true),
                    Episode = table.Column<int>(type: "integer", nullable: true),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Subtitle = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    PosterUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    BackdropUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    DurationSeconds = table.Column<long>(type: "bigint", nullable: false),
                    PositionSeconds = table.Column<long>(type: "bigint", nullable: false),
                    StartedAtEpoch = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAtEpoch = table.Column<long>(type: "bigint", nullable: false),
                    Finished = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_watch_history", x => x.Id);
                });

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "catalog_watch_history");
        }
    }
}
