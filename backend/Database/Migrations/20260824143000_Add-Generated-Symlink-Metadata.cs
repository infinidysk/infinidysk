using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations;

/// <summary>
/// Retains the filesystem location and target chosen when an optional symlink
/// output is created, allowing ownership-safe cleanup after settings change.
/// </summary>
public partial class AddGeneratedSymlinkMetadata : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "GeneratedStrmOutputRoot",
            table: "DavItems",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "GeneratedStrmPath",
            table: "DavItems",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "GeneratedStrmTarget",
            table: "DavItems",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "GeneratedSymlinkOutputRoot",
            table: "DavItems",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "GeneratedSymlinkPath",
            table: "DavItems",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "GeneratedSymlinkTarget",
            table: "DavItems",
            type: "TEXT",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "GeneratedStrmOutputRoot", table: "DavItems");
        migrationBuilder.DropColumn(name: "GeneratedStrmPath", table: "DavItems");
        migrationBuilder.DropColumn(name: "GeneratedStrmTarget", table: "DavItems");
        migrationBuilder.DropColumn(name: "GeneratedSymlinkOutputRoot", table: "DavItems");
        migrationBuilder.DropColumn(name: "GeneratedSymlinkPath", table: "DavItems");
        migrationBuilder.DropColumn(name: "GeneratedSymlinkTarget", table: "DavItems");
    }
}
