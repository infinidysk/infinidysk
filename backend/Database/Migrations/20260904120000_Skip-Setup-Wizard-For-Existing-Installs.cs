using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NzbWebDAV.Database.Migrations;

/// <summary>
/// Existing installations predate the setup wizard and should continue to open on
/// Overview. Fresh databases have no account, provider, or library data while
/// migrations run, so their setup state remains pending. Additive; back up /config
/// before upgrading.
/// </summary>
[DbContext(typeof(DavDatabaseContext))]
[Migration("20260904120000_Skip-Setup-Wizard-For-Existing-Installs")]
public partial class SkipSetupWizardForExistingInstalls : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            INSERT INTO "SetupWizardStates"
                ("Id", "WizardVersion", "Disposition", "IngestionMethods", "UpdatedAt")
            SELECT 1, 1, 2, '[]', unixepoch()
            WHERE NOT EXISTS (
                SELECT 1 FROM "SetupWizardStates" WHERE "Id" = 1
            )
            AND (
                EXISTS (SELECT 1 FROM "Accounts")
                OR EXISTS (
                    SELECT 1 FROM "ConfigItems" WHERE "ConfigName" = 'usenet.providers'
                )
                -- Fresh databases contain five system roots plus /content/uncategorized.
                OR (SELECT COUNT(*) FROM "DavItems") > 6
                OR EXISTS (SELECT 1 FROM "QueueItems")
                OR EXISTS (SELECT 1 FROM "HistoryItems")
            );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Keep the compatible skipped state so a downgrade does not force setup.
    }
}
