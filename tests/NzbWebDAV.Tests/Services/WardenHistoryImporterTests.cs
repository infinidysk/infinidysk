using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.Database;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Services;

[Collection(nameof(ConfigPathCollection))]
public sealed class WardenHistoryImporterTests : IAsyncLifetime
{
    private readonly string _configRoot =
        Path.Join(Path.GetTempPath(), $"nzbdav-warden-history-{Guid.NewGuid():N}");
    private string? _previousConfigPath;

    public Task InitializeAsync()
    {
        _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
        Directory.CreateDirectory(_configRoot);
        Environment.SetEnvironmentVariable("CONFIG_PATH", _configRoot);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
        try { Directory.Delete(_configRoot, recursive: true); } catch (IOException) { /* best effort */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Import_MintsFingerprintsFromMissingArticleFailures()
    {
        var deadBlob = Guid.NewGuid();
        var brokenArchiveBlob = Guid.NewGuid();
        var completedBlob = Guid.NewGuid();
        var blobs = new Dictionary<Guid, byte[]>
        {
            [deadBlob] = Nzb("dead@example.test", unixDate: 1_700_000_000),
            [brokenArchiveBlob] = Nzb("broken@example.test", unixDate: 1_700_100_000),
            [completedBlob] = Nzb("fine@example.test", unixDate: 1_700_200_000),
        };

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>().UseSqlite(connection).Options;
        await using (var context = new DavDatabaseContext(options))
        {
            await context.Database.EnsureCreatedAsync();
            context.HistoryItems.AddRange(
                Failed(deadBlob, "Missing segments across all providers.", 1_000),
                Failed(brokenArchiveBlob, "Could not parse the rar headers.", 2_000),
                new HistoryItem
                {
                    Id = Guid.NewGuid(),
                    CreatedAt = DateTime.UtcNow,
                    FileName = "fine.nzb",
                    JobName = "Fine.Release",
                    Category = "movies",
                    DownloadStatus = HistoryItem.DownloadStatusOption.Completed,
                    TotalSegmentBytes = 3_000,
                    NzbBlobId = completedBlob,
                });
            await context.SaveChangesAsync();
        }

        var warden = new WardenStore(new ConfigManager());
        var importer = new WardenHistoryImporter(
            new TestDbContextFactory(options), new MemoryBlobStore(blobs), warden);

        var preview = await importer.ImportAsync(dryRun: true);
        Assert.NotNull(preview);

        Assert.True(preview.DryRun);
        Assert.Equal(2, preview.Scanned);
        Assert.Equal(1, preview.Eligible);
        Assert.Equal(1, preview.Distinct);
        Assert.Equal(0, preview.Added);
        Assert.Equal(0, warden.LocalCount);

        var applied = await importer.ImportAsync(dryRun: false);
        Assert.NotNull(applied);

        Assert.False(applied.DryRun);
        Assert.Equal(1, applied.Added);
        Assert.Equal(1, warden.LocalCount);
        var expected = WardenFingerprint.Compute(
            1_000, "dead@example.test", DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        Assert.True(warden.IsDeadAnywhere(expected));
    }

    [Fact]
    public async Task Import_DedupesRepeatGrabsAndCountsSkips()
    {
        var sharedBlob = Guid.NewGuid();
        var missingBlob = Guid.NewGuid();
        var unparsableBlob = Guid.NewGuid();
        var undatedBlob = Guid.NewGuid();
        var blobs = new Dictionary<Guid, byte[]>
        {
            [sharedBlob] = Nzb("repeat@example.test", unixDate: 1_700_000_000),
            [unparsableBlob] = "not an nzb"u8.ToArray(),
            [undatedBlob] = Nzb(poster: null, unixDate: null),
        };

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>().UseSqlite(connection).Options;
        await using (var context = new DavDatabaseContext(options))
        {
            await context.Database.EnsureCreatedAsync();
            context.HistoryItems.AddRange(
                // The same release grabbed twice fingerprints identically, so it must add once.
                Failed(sharedBlob, "Missing segments across all providers.", 4_096),
                Failed(sharedBlob, "Missing rar volumes detected.", 4_096),
                Failed(missingBlob, "Missing segments across all providers.", 512),
                Failed(unparsableBlob, "Missing segments across all providers.", 512),
                Failed(undatedBlob, "Missing segments across all providers.", 512));
            await context.SaveChangesAsync();
        }

        var warden = new WardenStore(new ConfigManager());
        var importer = new WardenHistoryImporter(
            new TestDbContextFactory(options), new MemoryBlobStore(blobs), warden);

        var result = await importer.ImportAsync(dryRun: false);
        Assert.NotNull(result);

        Assert.Equal(5, result.Scanned);
        Assert.Equal(5, result.Eligible);
        Assert.Equal(2, result.Fingerprinted);
        Assert.Equal(1, result.Distinct);
        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.SkippedMissingNzb);
        Assert.Equal(1, result.SkippedUnparsableNzb);
        Assert.Equal(1, result.SkippedNoFingerprint);
        Assert.Equal(1, warden.LocalCount);
    }

    [Fact]
    public async Task Import_RefusesToStartWhileAScanIsRunning()
    {
        var blobId = Guid.NewGuid();
        var blobs = new Dictionary<Guid, byte[]> { [blobId] = Nzb("busy@example.test", 1_700_000_000) };

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DavDatabaseContext>().UseSqlite(connection).Options;
        await using (var context = new DavDatabaseContext(options))
        {
            await context.Database.EnsureCreatedAsync();
            context.HistoryItems.Add(Failed(blobId, "Missing segments across all providers.", 1_000));
            await context.SaveChangesAsync();
        }

        using var release = new ManualResetEventSlim(false);
        var blobStore = new BlockingBlobStore(blobs, release);
        var importer = new WardenHistoryImporter(
            new TestDbContextFactory(options), blobStore, new WardenStore(new ConfigManager()));

        var running = Task.Run(() => importer.ImportAsync(dryRun: true));
        Assert.True(blobStore.Entered.Wait(TimeSpan.FromSeconds(30)));

        Assert.Null(await importer.ImportAsync(dryRun: true));

        release.Set();
        Assert.NotNull(await running);
        // The gate reopens, so the next scan is free to run.
        Assert.NotNull(await importer.ImportAsync(dryRun: true));
    }

    [Theory]
    [InlineData("Missing segments across all providers.", true)]
    [InlineData("missing segments across all providers", true)]
    [InlineData("Missing rar volumes detected.", true)]
    [InlineData("Could not parse the rar headers.", false)]
    [InlineData("The archive is encrypted.", false)]
    [InlineData("No media files found.", false)]
    [InlineData(null, false)]
    public void IsDeadFailure_OnlyMatchesMissingArticleEvidence(string? failMessage, bool expected)
        => Assert.Equal(expected, WardenHistoryImporter.IsDeadFailure(failMessage));

    [Fact]
    public async Task ComputeFingerprint_FallsBackToSummedSegmentBytes()
    {
        await using var stream = new MemoryStream(Nzb("poster@example.test", 1_700_000_000));
        var nzb = await NzbDocument.LoadAsync(stream);

        var fromSegments = WardenHistoryImporter.ComputeFingerprint(nzb, totalBytes: 0);

        Assert.Equal(
            WardenFingerprint.Compute(128, "poster@example.test", DateTimeOffset.FromUnixTimeSeconds(1_700_000_000)),
            fromSegments);
    }

    private static HistoryItem Failed(Guid? blobId, string failMessage, long totalBytes) => new()
    {
        Id = Guid.NewGuid(),
        CreatedAt = DateTime.UtcNow,
        FileName = "release.nzb",
        JobName = "Some.Release",
        Category = "tv",
        DownloadStatus = HistoryItem.DownloadStatusOption.Failed,
        TotalSegmentBytes = totalBytes,
        FailMessage = failMessage,
        NzbBlobId = blobId,
    };

    private static byte[] Nzb(string? poster, long? unixDate) => Encoding.UTF8.GetBytes(
        $"""
         <?xml version="1.0" encoding="utf-8"?>
         <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
           <file {(poster is null ? "" : $"poster=\"{poster}\" ")}{(unixDate is null ? "" : $"date=\"{unixDate}\" ")}subject="sample.mkv">
             <groups><group>alt.binaries.test</group></groups>
             <segments>
               <segment bytes="128" number="1">seg1@example.test</segment>
             </segments>
           </file>
         </nzb>
         """);

    private sealed class TestDbContextFactory(DbContextOptions<DavDatabaseContext> options)
        : IDbContextFactory<DavDatabaseContext>
    {
        public DavDatabaseContext CreateDbContext() => new(options);

        public Task<DavDatabaseContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new DavDatabaseContext(options));
    }

    /// <summary>Holds the first read open so a second scan can be attempted mid-run.</summary>
    private sealed class BlockingBlobStore(Dictionary<Guid, byte[]> blobs, ManualResetEventSlim release)
        : MemoryBlobStore(blobs)
    {
        public ManualResetEventSlim Entered { get; } = new(false);

        public override Stream? ReadBlob(Guid id)
        {
            Entered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            return base.ReadBlob(id);
        }
    }

    private class MemoryBlobStore(Dictionary<Guid, byte[]> blobs) : IBlobStore
    {
        public Task WriteBlob(Guid id, Stream stream, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task WriteBlob<T>(Guid id, T blob, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public virtual Stream? ReadBlob(Guid id) =>
            blobs.TryGetValue(id, out var bytes) ? new MemoryStream(bytes) : null;

        public Task<T?> ReadBlob<T>(Guid id) => throw new NotSupportedException();

        public bool Exists(Guid id) => blobs.ContainsKey(id);

        public bool Delete(Guid id) => blobs.Remove(id);
    }
}
