using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.MetricsMigrations
{
    /// <inheritdoc />
    public partial class AddArrImportEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ArrImportEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    InstanceKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    ArrRecordId = table.Column<int>(type: "INTEGER", nullable: false),
                    DownloadId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ImportedAtMs = table.Column<long>(type: "INTEGER", nullable: false),
                    HandoffMs = table.Column<long>(type: "INTEGER", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArrImportEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArrImportEvents_ImportedAtMs",
                table: "ArrImportEvents",
                column: "ImportedAtMs");

            migrationBuilder.CreateIndex(
                name: "IX_ArrImportEvents_InstanceKey_ArrRecordId",
                table: "ArrImportEvents",
                columns: new[] { "InstanceKey", "ArrRecordId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ArrImportEvents_InstanceKey_ImportedAtMs",
                table: "ArrImportEvents",
                columns: new[] { "InstanceKey", "ImportedAtMs" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArrImportEvents");
        }
    }
}
