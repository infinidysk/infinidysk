using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;

namespace NzbWebDAV.Tests.Database;

public class DatabaseIntegrityCheckTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        "nzbdav-db-integrity-" + Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_tempDir, "db.sqlite");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task VerifyMainDatabaseAsync_HealthyDatabase_Passes()
    {
        await CreateMultiPageDatabaseAsync();
        await using var context = CreateContext();

        var ok = await DatabaseIntegrityCheck.VerifyMainDatabaseAsync(context);

        Assert.True(ok);
    }

    [Fact]
    public async Task VerifyMainDatabaseAsync_CorruptDatabase_IsDetected()
    {
        await CreateMultiPageDatabaseAsync();

        // Invalidate the b-tree page-type byte of page 2 (offset = page size).
        // Page 1 holds the 100-byte database header, so corruption is applied
        // past it to stay in the SQLITE_CORRUPT class rather than SQLITE_NOTADB.
        SqliteConnection.ClearAllPools();
        await using (var stream = new FileStream(DatabasePath, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Seek(4096, SeekOrigin.Begin);
            stream.WriteByte(0xFF);
        }

        await using var context = CreateContext();
        var ok = await DatabaseIntegrityCheck.VerifyMainDatabaseAsync(context);

        Assert.False(ok);
    }

    private DavDatabaseContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={DatabasePath}")
            .Options;
        return new DavDatabaseContext(options);
    }

    private async Task CreateMultiPageDatabaseAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        await connection.OpenAsync();

        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE payload (data TEXT NOT NULL);";
            await create.ExecuteNonQueryAsync();
        }

        // ~100 KB across many rows so the table spans well beyond page 2.
        var filler = new string('x', 500);
        for (var i = 0; i < 200; i++)
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO payload (data) VALUES ($data);";
            insert.Parameters.AddWithValue("$data", filler);
            await insert.ExecuteNonQueryAsync();
        }
    }
}
