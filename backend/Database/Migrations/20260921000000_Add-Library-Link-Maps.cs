using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;

#nullable disable

namespace NzbWebDAV.Database.Migrations;

/// <summary>
/// Persists discovered library symlink/STRM mappings for the read-only media
/// library catalog. Additive: new table only. Back up /config before upgrading.
/// </summary>
[DbContext(typeof(DavDatabaseContext))]
[Migration("20260921000000_Add-Library-Link-Maps")]
public partial class AddLibraryLinkMaps : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "LibraryLinkMaps",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                DavItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                LinkPath = table.Column<string>(type: "TEXT", nullable: false),
                TargetText = table.Column<string>(type: "TEXT", nullable: false),
                MappingType = table.Column<int>(type: "INTEGER", nullable: false),
                Status = table.Column<int>(type: "INTEGER", nullable: false),
                Size = table.Column<long>(type: "INTEGER", nullable: true),
                LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                LastCheckedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LibraryLinkMaps", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_LibraryLinkMaps_LinkPath",
            table: "LibraryLinkMaps",
            column: "LinkPath",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_LibraryLinkMaps_DavItemId",
            table: "LibraryLinkMaps",
            column: "DavItemId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "LibraryLinkMaps");
    }
}
