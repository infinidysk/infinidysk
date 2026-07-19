using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddCachedEpisodesTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CachedEpisodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DavItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", nullable: false),
                    FileSize = table.Column<long>(type: "INTEGER", nullable: false),
                    CachedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastAccessedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CachedEpisodes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CachedEpisodes_DavItemId",
                table: "CachedEpisodes",
                column: "DavItemId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CachedEpisodes_Status_LastAccessedAt",
                table: "CachedEpisodes",
                columns: new[] { "Status", "LastAccessedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CachedEpisodes");
        }
    }
}
