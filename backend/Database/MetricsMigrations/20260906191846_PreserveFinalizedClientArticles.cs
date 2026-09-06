using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.MetricsMigrations
{
    /// <inheritdoc />
    public partial class PreserveFinalizedClientArticles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ClientArticlesFinalized",
                table: "ThroughputMinutes",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ClientArticlesFinalized",
                table: "ProviderMinutes",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClientArticlesFinalized",
                table: "ThroughputMinutes");

            migrationBuilder.DropColumn(
                name: "ClientArticlesFinalized",
                table: "ProviderMinutes");
        }
    }
}
