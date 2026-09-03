using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations;

/// <summary>
/// The segment cache now defaults to off for new installs. Installs that already have
/// providers configured but never set the option keep their previous effective value
/// (on) by pinning it explicitly. Fresh databases have no providers row at this point,
/// so they fall through to the new default. Additive; back up /config before upgrading.
/// </summary>
[DbContext(typeof(DavDatabaseContext))]
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
            AND ("ConfigValue" IS NULL OR TRIM("ConfigValue") = '')
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
