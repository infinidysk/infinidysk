using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderUsageStatsTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProviderUsageStats",
                columns: table => new
                {
                    ProviderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderHost = table.Column<string>(type: "TEXT", nullable: false),
                    BytesDownloaded = table.Column<long>(type: "INTEGER", nullable: false),
                    ArticlesNotFoundCount = table.Column<long>(type: "INTEGER", nullable: false),
                    LastUpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderUsageStats", x => x.ProviderId);
                });

            migrationBuilder.CreateTable(
                name: "ProviderUsageStatsDaily",
                columns: table => new
                {
                    DateStartInclusive = table.Column<long>(type: "INTEGER", nullable: false),
                    DateEndExclusive = table.Column<long>(type: "INTEGER", nullable: false),
                    ProviderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BytesDownloaded = table.Column<long>(type: "INTEGER", nullable: false),
                    ArticlesNotFoundCount = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderUsageStatsDaily", x => new { x.DateStartInclusive, x.DateEndExclusive, x.ProviderId });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProviderUsageStatsDaily");

            migrationBuilder.DropTable(
                name: "ProviderUsageStats");
        }
    }
}
