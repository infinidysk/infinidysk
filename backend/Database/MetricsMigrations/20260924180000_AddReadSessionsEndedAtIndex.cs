using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NzbWebDAV.Database.MetricsMigrations;

[Migration("20260924180000_AddReadSessionsEndedAtIndex")]
[DbContext(typeof(MetricsDbContext))]
public class AddReadSessionsEndedAtIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // IF NOT EXISTS: operators may have created this index by hand as a workaround.
        migrationBuilder.Sql(
            "CREATE INDEX IF NOT EXISTS \"IX_ReadSessions_EndedAt\" ON \"ReadSessions\" (\"EndedAt\");");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_ReadSessions_EndedAt\";");
    }
}
