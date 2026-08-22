using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <summary>
    /// Adds a case-insensitive partial lookup index for completed NZB history
    /// rows used to annotate Search Profile streams with local ready state.
    /// </summary>
    [DbContext(typeof(DavDatabaseContext))]
    [Migration("20260822140000_Add-Profile-Stream-State-Index")]
    public partial class AddProfileStreamStateIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_HistoryItems_FileNameLower_Completed"
                ON "HistoryItems" (LOWER("FileName"))
                WHERE "DownloadStatus" = 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_HistoryItems_FileNameLower_Completed";""");
        }
    }
}
