using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;

#nullable disable

namespace NzbWebDAV.Database.PostgresMigrations;

/// <summary>
/// Persists discovered library symlink/STRM mappings for the read-only media
/// library catalog. Additive: new table only. Back up /config before upgrading.
/// </summary>
[DbContext(typeof(PostgresDavDatabaseContext))]
[Migration("20260921000000_Add-Library-Link-Maps")]
public partial class AddLibraryLinkMaps : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "LibraryLinkMaps",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                DavItemId = table.Column<Guid>(type: "uuid", nullable: true),
                LinkPath = table.Column<string>(type: "text", nullable: false),
                TargetText = table.Column<string>(type: "text", nullable: false),
                MappingType = table.Column<int>(type: "integer", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                Size = table.Column<long>(type: "bigint", nullable: true),
                LastSeenUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                LastCheckedUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
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
