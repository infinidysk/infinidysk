using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations;

[DbContext(typeof(DavDatabaseContext))]
[Migration("20260921210000_Add-Health-Alert-History-Lookup-Index")]
public partial class AddHealthAlertHistoryLookupIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_HealthCheckResults_DavItemId_CreatedAt_Id",
            table: "HealthCheckResults",
            columns: new[] { "DavItemId", "CreatedAt", "Id" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_HealthCheckResults_DavItemId_CreatedAt_Id",
            table: "HealthCheckResults");
    }
}