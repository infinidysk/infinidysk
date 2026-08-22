using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddNzbIdentityToHealthCheckResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "JobName",
                table: "HealthCheckResults",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NzbFileName",
                table: "HealthCheckResults",
                type: "TEXT",
                nullable: true);

            // Replace the partial/manual index state reported in #1104 with the
            // canonical filtered index created by this migration.
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_HealthCheckResults_RepairStatus_CreatedAt";""");

            migrationBuilder.CreateIndex(
                name: "IX_HealthCheckResults_RepairStatus_CreatedAt",
                table: "HealthCheckResults",
                columns: new[] { "RepairStatus", "CreatedAt" },
                filter: "\"RepairStatus\" IN (1, 2)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_HealthCheckResults_RepairStatus_CreatedAt",
                table: "HealthCheckResults");

            migrationBuilder.DropColumn(
                name: "JobName",
                table: "HealthCheckResults");

            migrationBuilder.DropColumn(
                name: "NzbFileName",
                table: "HealthCheckResults");
        }
    }
}
