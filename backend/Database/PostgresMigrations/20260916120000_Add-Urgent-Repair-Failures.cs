using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;

#nullable disable

namespace NzbWebDAV.Database.PostgresMigrations;

/// <summary>
/// Stores the streaming-failure count that qualified an urgent repair so a restart
/// cannot demote it. Additive: existing rows default to NULL. Back up /config before upgrading.
/// </summary>
[DbContext(typeof(PostgresDavDatabaseContext))]
[Migration("20260916120000_Add-Urgent-Repair-Failures")]
public partial class AddUrgentRepairFailures : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "UrgentRepairFailures",
            table: "DavItems",
            type: "integer",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "UrgentRepairFailures",
            table: "DavItems");
    }
}
