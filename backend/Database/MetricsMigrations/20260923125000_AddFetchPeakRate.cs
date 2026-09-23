using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.MetricsMigrations;

[Migration("20260923125000_AddFetchPeakRate")]
[DbContext(typeof(MetricsDbContext))]
public partial class AddFetchPeakRate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "PeakFetchBytesPerSec",
            table: "ThroughputMinutes",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.CreateTable(
            name: "ThroughputHourly",
            columns: table => new
            {
                Hour = table.Column<long>(type: "INTEGER", nullable: false),
                PeakFetchBytesPerSec = table.Column<long>(type: "INTEGER", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ThroughputHourly", x => x.Hour);
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ThroughputHourly");
        migrationBuilder.DropColumn(name: "PeakFetchBytesPerSec", table: "ThroughputMinutes");
    }
}
