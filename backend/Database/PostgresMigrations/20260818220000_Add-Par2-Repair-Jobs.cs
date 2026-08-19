using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.PostgresMigrations;

[DbContext(typeof(PostgresDavDatabaseContext))]
[Migration("20260818220000_Add-Par2-Repair-Jobs")]
public partial class AddPar2RepairJobs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Par2RepairJobs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                DavItemId = table.Column<Guid>(type: "uuid", nullable: false),
                Path = table.Column<string>(type: "text", nullable: false),
                State = table.Column<int>(type: "integer", nullable: false),
                MissingSegmentIds = table.Column<string>(type: "text", nullable: false),
                CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                StartedAt = table.Column<long>(type: "bigint", nullable: true),
                CompletedAt = table.Column<long>(type: "bigint", nullable: true),
                Attempts = table.Column<int>(type: "integer", nullable: false),
                NextAttemptAt = table.Column<long>(type: "bigint", nullable: true),
                FailureReason = table.Column<string>(type: "text", nullable: true),
                BytesRead = table.Column<long>(type: "bigint", nullable: false),
                SlicesReconstructed = table.Column<int>(type: "integer", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Par2RepairJobs", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Par2RepairJobs_DavItemId",
            table: "Par2RepairJobs",
            column: "DavItemId");

        migrationBuilder.CreateIndex(
            name: "IX_Par2RepairJobs_State_NextAttemptAt",
            table: "Par2RepairJobs",
            columns: new[] { "State", "NextAttemptAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "Par2RepairJobs");
    }
}
