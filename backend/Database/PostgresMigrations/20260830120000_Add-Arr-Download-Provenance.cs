using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;

#nullable disable

namespace NzbWebDAV.Database.PostgresMigrations;

/// <summary>
/// Durable Arr grab provenance independent of SAB history rows and NZB blob keys.
/// Additive and nullable; existing rows stay null. Back up /config before upgrading.
/// </summary>
[DbContext(typeof(PostgresDavDatabaseContext))]
[Migration("20260830120000_Add-Arr-Download-Provenance")]
public partial class AddArrDownloadProvenance : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "ArrDownloadId",
            table: "QueueItems",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "ArrDownloadId",
            table: "HistoryItems",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "ArrDownloadId",
            table: "DavItems",
            type: "uuid",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ArrDownloadId",
            table: "DavItems");

        migrationBuilder.DropColumn(
            name: "ArrDownloadId",
            table: "HistoryItems");

        migrationBuilder.DropColumn(
            name: "ArrDownloadId",
            table: "QueueItems");
    }
}
