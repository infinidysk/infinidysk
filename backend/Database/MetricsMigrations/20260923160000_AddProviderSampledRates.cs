using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NzbWebDAV.Database.MetricsMigrations;

[Migration("20260923160000_AddProviderSampledRates")]
[DbContext(typeof(MetricsDbContext))]
public class AddProviderSampledRates : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var table in new[] { "ProviderMinutes", "ProviderHourly", "ProviderLifetimeTotals" })
        {
            migrationBuilder.AddColumn<long>(name: "PeakBytesPerSec", table: table, type: "INTEGER", nullable: true);
            migrationBuilder.AddColumn<long>(name: "ActiveBytes", table: table, type: "INTEGER", nullable: true);
            migrationBuilder.AddColumn<double>(name: "ActiveSeconds", table: table, type: "REAL", nullable: true);
        }
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var table in new[] { "ProviderMinutes", "ProviderHourly", "ProviderLifetimeTotals" })
        {
            migrationBuilder.DropColumn(name: "PeakBytesPerSec", table: table);
            migrationBuilder.DropColumn(name: "ActiveBytes", table: table);
            migrationBuilder.DropColumn(name: "ActiveSeconds", table: table);
        }
    }
}