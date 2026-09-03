using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.PostgresMigrations;

/// <summary>
/// The segment cache now defaults to off for new installs. Installs that already have
/// providers configured but never set the option keep their previous effective value
/// (on) by pinning it explicitly. Fresh databases have no providers row at this point,
/// so they fall through to the new default. Additive; back up /config before upgrading.
/// </summary>
[DbContext(typeof(PostgresDavDatabaseContext))]
[Migration("20260903120000_Pin-Segment-Cache-For-Existing-Installs")]
public partial class PinSegmentCacheForExistingInstalls : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // ConfigManager treats a blank persisted value as unset, so pin those too.
        migrationBuilder.Sql("""
            UPDATE "ConfigItems"
            SET "ConfigValue" = 'true'
            WHERE "ConfigName" = 'usenet.segment-cache.enabled'
            AND ("ConfigValue" IS NULL OR BTRIM(
                "ConfigValue",
                CHR(9) || CHR(10) || CHR(11) || CHR(12) || CHR(13) || CHR(32) ||
                CHR(133) || CHR(160) || CHR(5760) ||
                CHR(8192) || CHR(8193) || CHR(8194) || CHR(8195) || CHR(8196) ||
                CHR(8197) || CHR(8198) || CHR(8199) || CHR(8200) || CHR(8201) ||
                CHR(8202) || CHR(8232) || CHR(8233) || CHR(8239) || CHR(8287) ||
                CHR(12288)
            ) = '')
            AND EXISTS (
                SELECT 1 FROM "ConfigItems"
                WHERE "ConfigName" = 'usenet.providers'
            );
            """);

        migrationBuilder.Sql("""
            INSERT INTO "ConfigItems" ("ConfigName", "ConfigValue")
            SELECT 'usenet.segment-cache.enabled', 'true'
            WHERE NOT EXISTS (
                SELECT 1 FROM "ConfigItems"
                WHERE "ConfigName" = 'usenet.segment-cache.enabled'
            )
            AND EXISTS (
                SELECT 1 FROM "ConfigItems"
                WHERE "ConfigName" = 'usenet.providers'
            );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Intentionally left blank: the pinned value matches the pre-migration
        // effective default, so it is safe to keep on downgrade.
    }
}
