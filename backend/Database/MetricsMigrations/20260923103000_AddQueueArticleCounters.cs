using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.MetricsMigrations;

[Migration("20260923103000_AddQueueArticleCounters")]
[DbContext(typeof(MetricsDbContext))]
public partial class AddQueueArticleCounters : Migration
{
    private static readonly string[] Tables =
        ["ThroughputMinutes", "ProviderMinutes", "ProviderHourly", "ProviderLifetimeTotals"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var table in Tables)
        {
            migrationBuilder.AddColumn<long>(
                name: "QueueArticles",
                table: table,
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);
        }
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var table in Tables)
        {
            migrationBuilder.DropColumn(name: "QueueArticles", table: table);
        }
    }
}
