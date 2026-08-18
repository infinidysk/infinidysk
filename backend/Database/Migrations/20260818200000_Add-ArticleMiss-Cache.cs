using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations;

[DbContext(typeof(DavDatabaseContext))]
[Migration("20260818200000_Add-ArticleMiss-Cache")]
public partial class AddArticleMissCache : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ArticleMissCacheEntries",
            columns: table => new
            {
                CacheKey = table.Column<string>(type: "TEXT", nullable: false),
                ConfirmedAtUnix = table.Column<long>(type: "INTEGER", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ArticleMissCacheEntries", x => x.CacheKey);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ArticleMissCacheEntries_ConfirmedAtUnix",
            table: "ArticleMissCacheEntries",
            column: "ConfirmedAtUnix");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ArticleMissCacheEntries");
    }
}
