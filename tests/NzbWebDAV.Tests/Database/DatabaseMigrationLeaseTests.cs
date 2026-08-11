using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Exceptions;

namespace NzbWebDAV.Tests.Database;

public sealed class DatabaseMigrationLeaseTests
{
    private const string PriorMainMigration = "20260713120000_Add-Path-Index-To-DavItems";

    [Fact]
    public async Task AcquireAsync_SerializesCallersForTheSameDatabase()
    {
        var path = TempDatabasePath();
        try
        {
            await using var first = await DatabaseMigrationLease.AcquireAsync(path);
            var secondTask = DatabaseMigrationLease.AcquireAsync(path);

            await Task.Delay(TimeSpan.FromMilliseconds(100));
            Assert.False(secondTask.IsCompleted);

            await first.DisposeAsync();
            await using var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            DeleteDatabaseFiles(path);
        }
    }

    [Fact]
    public async Task ClearAbandonedMigrationLockAsync_UnblocksPendingMigration()
    {
        var path = TempDatabasePath();
        try
        {
            await using var context = CreateMainContext(path);
            await context.Database.MigrateAsync(PriorMainMigration);
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT OR REPLACE INTO "__EFMigrationsLock" ("Id", "Timestamp")
                VALUES (1, '2026-07-23 01:40:05+00:00')
                """);

            await using var lease = await DatabaseMigrationLease.AcquireAsync(path);
            await DatabaseStartupGuards.ClearAbandonedMigrationLockAsync(context);
            await context.Database.MigrateAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        }
        finally
        {
            DeleteDatabaseFiles(path);
        }
    }

    [Fact]
    public async Task ClearAbandonedMigrationLockAsync_IsNoOpBeforeLockTableExists()
    {
        var path = TempDatabasePath();
        try
        {
            await using var context = CreateMainContext(path);
            await using var lease = await DatabaseMigrationLease.AcquireAsync(path);

            await DatabaseStartupGuards.ClearAbandonedMigrationLockAsync(context);

            Assert.False(await DatabaseStartupGuards.TableExistsAsync(context, "__EFMigrationsLock"));
        }
        finally
        {
            DeleteDatabaseFiles(path);
        }
    }

    [Fact]
    public async Task AcquireAsync_IgnoresLeftoverUnlockedSidecarFile()
    {
        var path = TempDatabasePath();
        try
        {
            await File.WriteAllTextAsync(path + ".maintenance.lock", "left by interrupted process");

            await using var lease = await DatabaseMigrationLease
                .AcquireAsync(path)
                .WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            DeleteDatabaseFiles(path);
        }
    }

    [SkippableFact]
    public async Task AcquireAsync_ThrowsConfigPathAccessForUnreadableLeaseFile()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix file modes are not enforced on Windows.");
        var path = TempDatabasePath();
        var leasePath = path + ".maintenance.lock";
        try
        {
            await File.WriteAllTextAsync(leasePath, "left by root-owned install");
            SetMode(leasePath, UnixFileMode.None);
            Skip.IfNot(PermissionsEnforced(leasePath), "Test is running as root; file modes are not enforced.");

            var exception = await Assert.ThrowsAsync<ConfigPathAccessException>(
                () => DatabaseMigrationLease.AcquireAsync(path));

            Assert.Contains(leasePath, exception.Message);
            Assert.IsType<UnauthorizedAccessException>(exception.InnerException);
        }
        finally
        {
            if (File.Exists(leasePath))
                SetMode(leasePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            DeleteDatabaseFiles(path);
        }
    }

    // The guard keeps the platform analyzer satisfied; callers skip on Windows anyway.
    private static void SetMode(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, mode);
    }

    private static bool PermissionsEnforced(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            return false; // root bypasses mode bits
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static DavDatabaseContext CreateMainContext(string path)
    {
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={path};Pooling=False")
            .AddInterceptors(new SqliteMainDbPragmas())
            .ReplaceService<
                IMigrationsSqlGenerator,
                SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;
        return new DavDatabaseContext(options);
    }

    private static string TempDatabasePath() =>
        Path.Join(Path.GetTempPath(), $"nzbdav-migration-lease-{Guid.NewGuid():N}.sqlite");

    private static void DeleteDatabaseFiles(string path)
    {
        TryDelete(path);
        TryDelete(path + "-wal");
        TryDelete(path + "-shm");
        TryDelete(path + ".maintenance.lock");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { /* best effort */ }
    }
}
