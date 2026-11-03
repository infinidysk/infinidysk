using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
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
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    TmdbId = table.Column<int>(type: "INTEGER", nullable: false),
                    Season = table.Column<int>(type: "INTEGER", nullable: true),
                    Episode = table.Column<int>(type: "INTEGER", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Subtitle = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    PosterUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    BackdropUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    DurationSeconds = table.Column<long>(type: "INTEGER", nullable: false),
                    PositionSeconds = table.Column<long>(type: "INTEGER", nullable: false),
                    StartedAtEpoch = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtEpoch = table.Column<long>(type: "INTEGER", nullable: false),
                    Finished = table.Column<bool>(type: "INTEGER", nullable: false)
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
