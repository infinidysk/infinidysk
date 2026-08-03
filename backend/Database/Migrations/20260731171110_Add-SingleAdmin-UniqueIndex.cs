using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddSingleAdminUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Repair databases that already contain duplicate admins before enforcing
            // the invariant. Keep the earliest row, which is the first admin created.
            migrationBuilder.Sql("""
                DELETE FROM Accounts
                WHERE Type = 1
                  AND rowid NOT IN (
                      SELECT MIN(rowid)
                      FROM Accounts
                      WHERE Type = 1
                  );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_SingleAdmin",
                table: "Accounts",
                column: "Type",
                unique: true,
                filter: "\"Type\" = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Accounts_SingleAdmin",
                table: "Accounts");
        }
    }
}
