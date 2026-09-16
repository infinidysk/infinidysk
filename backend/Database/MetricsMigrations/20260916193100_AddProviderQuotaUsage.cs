using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using NzbWebDAV.Database;

#nullable disable

namespace NzbWebDAV.Database.MetricsMigrations;

[Migration("20260916193100_AddProviderQuotaUsage")]
[DbContext(typeof(MetricsDbContext))]
public partial class AddProviderQuotaUsage : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ProviderQuotaUsage",
            columns: table => new
            {
                Provider = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                BytesUsed = table.Column<long>(type: "INTEGER", nullable: false),
                ResetAt = table.Column<long>(type: "INTEGER", nullable: false),
                UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ProviderQuotaUsage", x => x.Provider);
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ProviderQuotaUsage");
    }
}