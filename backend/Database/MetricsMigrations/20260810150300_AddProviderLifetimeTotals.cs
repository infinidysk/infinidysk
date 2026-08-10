using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.MetricsMigrations
{
    /// <inheritdoc />
    public partial class AddProviderLifetimeTotals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProviderLifetimeTotals",
                columns: table => new
                {
                    Provider = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    BytesFetched = table.Column<long>(type: "INTEGER", nullable: false),
                    Articles = table.Column<long>(type: "INTEGER", nullable: false),
                    Misses = table.Column<long>(type: "INTEGER", nullable: false),
                    Errors = table.Column<long>(type: "INTEGER", nullable: false),
                    Retries = table.Column<long>(type: "INTEGER", nullable: false),
                    SumDurationMs = table.Column<long>(type: "INTEGER", nullable: false),
                    FailoverSaves = table.Column<long>(type: "INTEGER", nullable: false),
                    FirstHour = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderLifetimeTotals", x => x.Provider);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProviderLifetimeTotals");
        }
    }
}
