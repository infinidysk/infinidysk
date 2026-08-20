using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.MigrationHelpers;

#nullable disable

namespace NzbWebDAV.Database.Migrations
{
    /// <summary>
    /// Rewrites mixed-case GUID TEXT to uppercase so Microsoft.Data.Sqlite parameter
    /// binding matches stored values. SQLite TEXT comparisons are case-sensitive;
    /// lowercase Ids seeded by historical SQL (for example Fix-Empty-Categories) were
    /// invisible to EF queries and could poison cleanup queues.
    /// <para>
    /// PostgreSQL is not affected (native uuid). Do not port this migration.
    /// Future SQL-generated GUIDs must use uppercase hex to match parameter binding.
    /// </para>
    /// </summary>
    [DbContext(typeof(DavDatabaseContext))]
    [Migration(GuidTextCasingSql.MigrationId)]
    public partial class NormalizeGuidTextCasing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // PRAGMA foreign_keys cannot change inside a transaction. Defer checks
            // until COMMIT instead so the PK/FK rewrite stays in the migration
            // transaction with __EFMigrationsHistory (crash-safe retry).
            migrationBuilder.Sql("PRAGMA defer_foreign_keys = ON;");

            // Updating FileBlobId from lower to upper would otherwise fire this
            // trigger and enqueue the old (lowercase) blob Id for deletion.
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_DavItems_Update_AddBlobCleanup;");

            // Cleanup queues can already contain the same Id in both casings when
            // TR_DavItems_Update_AddBlobCleanup copied a FileBlobId that later
            // differed only by case. Fold those before the PK abort check.
            migrationBuilder.Sql(GuidTextCasingSql.DedupeCleanupItemsSql);
            migrationBuilder.Sql(GuidTextCasingSql.AbortIfDuplicateIdsSql);
            migrationBuilder.Sql(GuidTextCasingSql.RenameParentNameCollisionsSql);

            foreach (var statement in GuidTextCasingSql.UppercaseTableStatements)
                migrationBuilder.Sql(statement);

            AddPathToDavItem.BuildFullPath(migrationBuilder);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER TR_DavItems_Update_AddBlobCleanup
                AFTER UPDATE OF FileBlobId ON DavItems
                WHEN OLD.FileBlobId IS NOT NULL AND OLD.FileBlobId != NEW.FileBlobId
                BEGIN
                    INSERT INTO BlobCleanupItems (Id)
                    VALUES (OLD.FileBlobId);
                END;
                """);

            // Deferred FK checks run at COMMIT; no extra PRAGMA is required.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally left blank — data repair is not reversible.
        }
    }
}
