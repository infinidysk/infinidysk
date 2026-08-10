using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Backup;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Database;

[Collection(nameof(ConfigPathCollection))]
public sealed class SqliteDumpImportTests
{
    [Fact]
    public async Task DumpAndImport_RoundTripsRepresentativeContent()
    {
        var root = Path.Join(Path.GetTempPath(), $"nzbdav-dump-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var sourcePath = Path.Join(root, "source.sqlite");
            var dumpPath = Path.Join(root, "dump.sql");
            var restoredPath = Path.Join(root, "restored.sqlite");
            var redumpPath = Path.Join(root, "redump.sql");

            await CreateRepresentativeDatabaseAsync(sourcePath);

            await SqliteDumper.DumpToFileAsync(sourcePath, dumpPath);
            await SqliteSqlImporter.ImportAsync(dumpPath, restoredPath);

            await using (var connection = Open(restoredPath))
            {
                await connection.OpenAsync();

                await using (var countCmd = connection.CreateCommand())
                {
                    countCmd.CommandText = "SELECT COUNT(*) FROM Notes;";
                    Assert.Equal(3L, (long)(await countCmd.ExecuteScalarAsync())!);
                }

                await using (var textCmd = connection.CreateCommand())
                {
                    textCmd.CommandText = "SELECT Body FROM Notes WHERE Id = 2;";
                    Assert.Equal("line1; still one value\nwith 'quotes'", (string)(await textCmd.ExecuteScalarAsync())!);
                }

                await using (var realCmd = connection.CreateCommand())
                {
                    realCmd.CommandText = "SELECT Amount FROM Payments WHERE Id = 1;";
                    Assert.Equal(0.1, (double)(await realCmd.ExecuteScalarAsync())!);
                }

                await using (var blobCmd = connection.CreateCommand())
                {
                    blobCmd.CommandText = "SELECT Payload FROM Blobs WHERE Id = 1;";
                    var bytes = (byte[])(await blobCmd.ExecuteScalarAsync())!;
                    Assert.Equal(new byte[] { 0x00, 0xFF, 0x10 }, bytes);
                }

                await using (var auditBefore = connection.CreateCommand())
                {
                    auditBefore.CommandText = "SELECT COUNT(*) FROM AuditLog;";
                    var before = (long)(await auditBefore.ExecuteScalarAsync())!;
                    await using (var triggerCmd = connection.CreateCommand())
                    {
                        triggerCmd.CommandText = "INSERT INTO Notes (Body) VALUES ('trigger-me');";
                        await triggerCmd.ExecuteNonQueryAsync();
                    }

                    await using var auditAfter = connection.CreateCommand();
                    auditAfter.CommandText = "SELECT COUNT(*) FROM AuditLog;";
                    Assert.Equal(before + 1, (long)(await auditAfter.ExecuteScalarAsync())!);
                }

                await using (var viewCmd = connection.CreateCommand())
                {
                    viewCmd.CommandText = "SELECT COUNT(*) FROM NotesView;";
                    Assert.True((long)(await viewCmd.ExecuteScalarAsync())! >= 4);
                }
            }

            await SqliteDumper.DumpToFileAsync(restoredPath, redumpPath);
            // Second dump of an imported DB should contain the same schema objects.
            var dumpText = await File.ReadAllTextAsync(dumpPath);
            var redumpText = await File.ReadAllTextAsync(redumpPath);
            Assert.Contains("CREATE TABLE", dumpText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("CREATE TRIGGER", redumpText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("CREATE VIEW", redumpText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("CREATE INDEX", redumpText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Importer_HandlesSemicolonsCommentsAndMultilineStatements()
    {
        var root = Path.Join(Path.GetTempPath(), $"nzbdav-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var sqlPath = Path.Join(root, "special.sql");
            var dbPath = Path.Join(root, "special.sqlite");
            await File.WriteAllTextAsync(sqlPath, """
                PRAGMA foreign_keys=OFF;
                BEGIN TRANSACTION;
                -- this is a comment; with a semicolon
                CREATE TABLE Items (
                  Id INTEGER PRIMARY KEY,
                  Name TEXT
                );
                /* multi-line
                   comment; still ignored */
                INSERT INTO Items (Id, Name) VALUES (1, 'a;b');
                INSERT INTO Items (Id, Name) VALUES (2, 'line1
                line2');
                COMMIT;
                """);

            Assert.True(SqliteSqlImporter.IsCompleteStatement("SELECT 1;"));
            Assert.False(SqliteSqlImporter.IsCompleteStatement("INSERT INTO t VALUES ('a;"));
            Assert.True(SqliteSqlImporter.IsCompleteStatement("INSERT INTO t VALUES ('a;b');"));

            await SqliteSqlImporter.ImportAsync(sqlPath, dbPath);

            await using var connection = Open(dbPath);
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Name FROM Items ORDER BY Id;";
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("a;b", reader.GetString(0));
            Assert.True(await reader.ReadAsync());
            Assert.Equal("line1\nline2", reader.GetString(0));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task Importer_RejectsAttachedDatabaseAndLeavesItUnchanged()
    {
        var root = Path.Join(Path.GetTempPath(), $"nzbdav-import-authorizer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var sqlPath = Path.Join(root, "malicious.sql");
            var stagedPath = Path.Join(root, "staged.sqlite");
            var livePath = Path.Join(root, "live.sqlite");
            await File.WriteAllTextAsync(sqlPath, $"""
                CREATE TABLE Safe(Value TEXT);
                aTtAcH DATABASE '{livePath.Replace("'", "''", StringComparison.Ordinal)}' AS live;
                DELETE FROM live.Marker;
                """);

            await using (var live = Open(livePath, create: true))
            {
                await live.OpenAsync();
                await using var create = live.CreateCommand();
                create.CommandText = "CREATE TABLE Marker(Value TEXT); INSERT INTO Marker VALUES ('live');";
                await create.ExecuteNonQueryAsync();
            }

            var error = await Assert.ThrowsAsync<SqliteException>(
                () => SqliteSqlImporter.ImportAsync(sqlPath, stagedPath));

            Assert.Contains("not authorized", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(stagedPath));

            await using var check = Open(livePath);
            await check.OpenAsync();
            await using var marker = check.CreateCommand();
            marker.CommandText = "SELECT Value FROM Marker;";
            Assert.Equal("live", await marker.ExecuteScalarAsync());
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task DumpAndImport_RoundTripsRealDavSchema()
    {
        var root = Path.Join(Path.GetTempPath(), $"nzbdav-schema-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var sourceDb = Path.Join(root, "source.sqlite");
        var dumpPath = Path.Join(root, "db.sql");
        var restoredDb = Path.Join(root, "restored-db.sqlite");
        try
        {
            var sourceOptions = new DbContextOptionsBuilder<DavDatabaseContext>()
                .UseSqlite($"Data Source={sourceDb};Pooling=False")
                .AddInterceptors(new NzbWebDAV.Database.Interceptors.SqliteMainDbPragmas())
                .ReplaceService<
                    Microsoft.EntityFrameworkCore.Migrations.IMigrationsSqlGenerator,
                    NzbWebDAV.Database.MigrationHelpers.SqliteMigrationsSqlGenerator<
                        Microsoft.EntityFrameworkCore.Migrations.SqliteMigrationsSqlGenerator>>()
                .Options;

            await using (var ctx = new DavDatabaseContext(sourceOptions))
            {
                await ctx.Database.MigrateAsync();
                ctx.ConfigItems.Add(new NzbWebDAV.Database.Models.ConfigItem
                {
                    ConfigName = "test.key",
                    ConfigValue = "test-value",
                });
                await ctx.SaveChangesAsync();
            }

            await SqliteDumper.DumpToFileAsync(sourceDb, dumpPath);
            await SqliteSqlImporter.ImportAsync(dumpPath, restoredDb, requireMigrationsHistory: true);

            var restoredOptions = new DbContextOptionsBuilder<DavDatabaseContext>()
                .UseSqlite($"Data Source={restoredDb};Pooling=False")
                .AddInterceptors(new NzbWebDAV.Database.Interceptors.SqliteMainDbPragmas())
                .ReplaceService<
                    Microsoft.EntityFrameworkCore.Migrations.IMigrationsSqlGenerator,
                    NzbWebDAV.Database.MigrationHelpers.SqliteMigrationsSqlGenerator<
                        Microsoft.EntityFrameworkCore.Migrations.SqliteMigrationsSqlGenerator>>()
                .Options;
            await using (var restored = new DavDatabaseContext(restoredOptions))
            {
                var pending = await restored.Database.GetPendingMigrationsAsync();
                Assert.Empty(pending);

                var value = await restored.ConfigItems
                    .Where(x => x.ConfigName == "test.key")
                    .Select(x => x.ConfigValue)
                    .SingleAsync();
                Assert.Equal("test-value", value);

                await using var connection = restored.Database.GetDbConnection();
                await connection.OpenAsync();
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = """
                    SELECT COUNT(*) FROM sqlite_master
                    WHERE type = 'trigger' AND name LIKE 'TR_%';
                    """;
                var triggerCount = Convert.ToInt64(await cmd.ExecuteScalarAsync());
                Assert.True(triggerCount > 0);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteDirectory(root);
        }
    }

    private static async Task CreateRepresentativeDatabaseAsync(string path)
    {
        await using var connection = Open(path, create: true);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE Notes (
              Id INTEGER PRIMARY KEY AUTOINCREMENT,
              Body TEXT,
              Flag INTEGER
            );
            CREATE TABLE Payments (
              Id INTEGER PRIMARY KEY,
              Amount REAL
            );
            CREATE TABLE Blobs (
              Id INTEGER PRIMARY KEY,
              Payload BLOB
            );
            CREATE TABLE AuditLog (
              Id INTEGER PRIMARY KEY AUTOINCREMENT,
              NoteId INTEGER
            );
            CREATE INDEX IX_Notes_Body ON Notes(Body);
            CREATE TRIGGER TR_Notes_Insert
            AFTER INSERT ON Notes
            BEGIN
              INSERT INTO AuditLog(NoteId) VALUES (NEW.Id);
            END;
            CREATE VIEW NotesView AS SELECT Id, Body FROM Notes;
            INSERT INTO Notes (Body, Flag) VALUES ('plain', NULL);
            INSERT INTO Notes (Body, Flag) VALUES ('line1; still one value
            with ''quotes''', 1);
            INSERT INTO Notes (Body, Flag) VALUES (NULL, 0);
            INSERT INTO Payments (Id, Amount) VALUES (1, 0.1);
            INSERT INTO Blobs (Id, Payload) VALUES (1, X'00FF10');
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private static SqliteConnection Open(string path, bool create = false)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();
        return new SqliteConnection(cs);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // best effort
        }
    }
}

[Collection(nameof(ConfigPathCollection))]
public sealed class DatabaseBackupStoreTests
{
    [Fact]
    public void Retention_RespectsPreservedAndZeroDisablesPruning()
    {
        var root = Path.Join(Path.GetTempPath(), $"nzbdav-store-{Guid.NewGuid():N}");
        var previous = Environment.GetEnvironmentVariable("CONFIG_PATH");
        Environment.SetEnvironmentVariable("CONFIG_PATH", root);
        try
        {
            Directory.CreateDirectory(root);
            var store = new DatabaseBackupStore();
            store.EnsureInitialized();

            CreateCommittedBackup(store, "manual", preserved: false);
            CreateCommittedBackup(store, "manual", preserved: false);
            CreateCommittedBackup(store, "manual", preserved: true);
            CreateCommittedBackup(store, "manual", preserved: false);

            Assert.Equal(4, store.List().Count);
            Assert.Equal(0, store.Prune(0));
            Assert.Equal(4, store.List().Count);

            var pruned = store.Prune(1);
            Assert.Equal(2, pruned);
            var remaining = store.List();
            Assert.Equal(2, remaining.Count);
            Assert.Contains(remaining, x => x.Preserved);
            Assert.DoesNotContain(remaining, x => Path.GetFileName(store.GetBackupDirectory(x.Id)).StartsWith(".tmp-"));

            var preserved = remaining.Single(x => x.Preserved);
            store.UpdateManifest(preserved.Id, notes: "pinned");
            Assert.Equal("pinned", store.Get(preserved.Id)!.Notes);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CONFIG_PATH", previous);
            TryDelete(root);
        }
    }

    [Fact]
    public void IncompletePendingRestore_IsReadableAndClearable()
    {
        var root = Path.Join(Path.GetTempPath(), $"nzbdav-intent-{Guid.NewGuid():N}");
        var previous = Environment.GetEnvironmentVariable("CONFIG_PATH");
        Environment.SetEnvironmentVariable("CONFIG_PATH", root);
        try
        {
            var store = new DatabaseBackupStore();
            store.EnsureInitialized();
            store.WritePendingRestore(new PendingRestoreIntent
            {
                BackupId = "20260101-000000-manual",
                PreRestoreBackupId = "20260101-000001-pre-restore",
                StagedFiles = ["db.sqlite"],
                CreatedAt = DateTimeOffset.UtcNow,
            });
            Assert.True(store.HasPendingRestore());
            Assert.NotNull(store.ReadPendingRestore());
            store.ClearPendingRestore();
            Assert.False(store.HasPendingRestore());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CONFIG_PATH", previous);
            TryDelete(root);
        }
    }

    private static void CreateCommittedBackup(DatabaseBackupStore store, string kind, bool preserved)
    {
        var staging = store.CreateStaging(kind);
        File.WriteAllText(Path.Join(staging, DatabaseBackupStore.DbSqlName), "PRAGMA foreign_keys=OFF;\nBEGIN TRANSACTION;\nCOMMIT;\n");
        store.CommitStaging(staging, kind, notes: null, preserved: preserved, appVersion: "test", lastMainMigration: null);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // best effort
        }
    }
}

[Collection(nameof(ConfigPathCollection))]
public sealed class DatabaseRestoreRunnerTests
{
    [Fact]
    public async Task ApplyPendingRestore_DiscardsMissingStagingFiles()
    {
        var root = Path.Join(Path.GetTempPath(), $"nzbdav-restore-{Guid.NewGuid():N}");
        var previous = Environment.GetEnvironmentVariable("CONFIG_PATH");
        Environment.SetEnvironmentVariable("CONFIG_PATH", root);
        try
        {
            Directory.CreateDirectory(root);
            var store = new DatabaseBackupStore();
            store.EnsureInitialized();
            store.WritePendingRestore(new PendingRestoreIntent
            {
                BackupId = "missing-staging",
                PreRestoreBackupId = "pre",
                StagedFiles = ["db.sqlite"],
                CreatedAt = DateTimeOffset.UtcNow,
            });

            var progress = new MigrationProgress();
            progress.Initialize(DatabaseRestoreRunner.GetRestoreSteps(store.ReadPendingRestore()!));
            await DatabaseRestoreRunner.ApplyPendingRestoreAsync(progress);

            Assert.False(store.HasPendingRestore());
            Assert.False(Directory.Exists(store.RestoreStagingRoot));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CONFIG_PATH", previous);
            SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }

    [Fact]
    public async Task ApplyPendingRestore_SwapsStagedDatabaseAndWritesReport()
    {
        var root = Path.Join(Path.GetTempPath(), $"nzbdav-swap-{Guid.NewGuid():N}");
        var previous = Environment.GetEnvironmentVariable("CONFIG_PATH");
        Environment.SetEnvironmentVariable("CONFIG_PATH", root);
        try
        {
            Directory.CreateDirectory(root);
            var store = new DatabaseBackupStore();
            store.EnsureInitialized();

            // Live DB
            await using (var live = new SqliteConnection($"Data Source={DavDatabaseContext.DatabaseFilePath};Pooling=False"))
            {
                await live.OpenAsync();
                await using var cmd = live.CreateCommand();
                cmd.CommandText = "CREATE TABLE Marker(Value TEXT); INSERT INTO Marker VALUES ('live');";
                await cmd.ExecuteNonQueryAsync();
            }

            // Pre-restore backup folder for rollback target
            var preStaging = store.CreateStaging(DatabaseBackupKinds.PreRestore);
            File.WriteAllText(Path.Join(preStaging, DatabaseBackupStore.DbSqlName), "PRAGMA foreign_keys=OFF;\nBEGIN;\nCOMMIT;\n");
            var pre = store.CommitStaging(preStaging, DatabaseBackupKinds.PreRestore, "safety", preserved: true, "test", null);

            // Staged restored DB
            store.PrepareRestoreStaging();
            var stagedPath = Path.Join(store.RestoreStagingRoot, "db.sqlite");
            await using (var staged = new SqliteConnection($"Data Source={stagedPath};Pooling=False"))
            {
                await staged.OpenAsync();
                await using var cmd = staged.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE Marker(Value TEXT);
                    CREATE TABLE DavItems(FileBlobId TEXT, NzbBlobId TEXT);
                    CREATE TABLE HistoryItems(NzbBlobId TEXT);
                    INSERT INTO Marker VALUES ('restored');
                    """;
                await cmd.ExecuteNonQueryAsync();
            }

            store.WritePendingRestore(new PendingRestoreIntent
            {
                BackupId = "swap-test",
                PreRestoreBackupId = pre.Id,
                StagedFiles = ["db.sqlite"],
                CreatedAt = DateTimeOffset.UtcNow,
            });

            var progress = new MigrationProgress();
            progress.Initialize(DatabaseRestoreRunner.GetRestoreSteps(store.ReadPendingRestore()!));
            await DatabaseRestoreRunner.ApplyPendingRestoreAsync(progress);

            Assert.False(store.HasPendingRestore());
            await using (var live = new SqliteConnection($"Data Source={DavDatabaseContext.DatabaseFilePath};Pooling=False"))
            {
                await live.OpenAsync();
                await using var cmd = live.CreateCommand();
                cmd.CommandText = "SELECT Value FROM Marker;";
                Assert.Equal("restored", (string)(await cmd.ExecuteScalarAsync())!);
            }

            var rollbackDb = Path.Join(store.GetBackupDirectory(pre.Id), DatabaseBackupStore.RollbackFolderName, "db.sqlite");
            Assert.True(File.Exists(rollbackDb));

            var report = store.ReadLastRestoreReport();
            Assert.NotNull(report);
            Assert.Equal("swap-test", report!.BackupId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CONFIG_PATH", previous);
            SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }
}
