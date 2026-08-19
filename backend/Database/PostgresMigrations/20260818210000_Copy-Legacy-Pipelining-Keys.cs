using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.PostgresMigrations
{
    /// <inheritdoc />
    public partial class CopyLegacyPipeliningKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO "ConfigItems" ("ConfigName", "ConfigValue")
                SELECT 'usenet.queue-pipelining.enabled', "ConfigValue" FROM "ConfigItems"
                WHERE "ConfigName" = 'usenet.pipelining.enabled'
                  AND NOT EXISTS (
                      SELECT 1 FROM "ConfigItems"
                      WHERE "ConfigName" = 'usenet.queue-pipelining.enabled'
                  );
                """);

            migrationBuilder.Sql("""
                INSERT INTO "ConfigItems" ("ConfigName", "ConfigValue")
                SELECT 'usenet.queue-pipelining.depth', "ConfigValue" FROM "ConfigItems"
                WHERE "ConfigName" = 'usenet.pipelining.depth'
                  AND NOT EXISTS (
                      SELECT 1 FROM "ConfigItems"
                      WHERE "ConfigName" = 'usenet.queue-pipelining.depth'
                  );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM "ConfigItems"
                WHERE "ConfigName" IN (
                    'usenet.queue-pipelining.enabled',
                    'usenet.queue-pipelining.depth'
                );
                """);
        }
    }
}
