using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.MetricsMigrations
{
    /// <inheritdoc />
    public partial class AddClientArticleCounters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ClientArticles",
                table: "ThroughputMinutes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "Workload",
                table: "SegmentFetches",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "ClientArticles",
                table: "ProviderMinutes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "ClientArticles",
                table: "ProviderLifetimeTotals",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "ClientArticles",
                table: "ProviderHourly",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClientArticles",
                table: "ThroughputMinutes");

            migrationBuilder.DropColumn(
                name: "Workload",
                table: "SegmentFetches");

            migrationBuilder.DropColumn(
                name: "ClientArticles",
                table: "ProviderMinutes");

            migrationBuilder.DropColumn(
                name: "ClientArticles",
                table: "ProviderLifetimeTotals");

            migrationBuilder.DropColumn(
                name: "ClientArticles",
                table: "ProviderHourly");
        }
    }
}
