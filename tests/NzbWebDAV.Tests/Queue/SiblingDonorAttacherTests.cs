using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue.SiblingDonors;
using NzbWebDAV.Tests.Database;

namespace NzbWebDAV.Tests.Queue;

[Collection(nameof(ConfigPathCollection))]
public sealed class SiblingDonorAttacherTests : IAsyncLifetime
{
    private const string GroupKey = "movie:511";
    private readonly string _configRoot =
        Path.Join(Path.GetTempPath(), $"nzbdav-sibling-donors-{Guid.NewGuid():N}");
    private string? _previousConfigPath;
    private DavDatabaseContext _context = null!;
    private DavDatabaseClient _dbClient = null!;
    private ConfigManager _config = null!;

    public async Task InitializeAsync()
    {
        _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
        Directory.CreateDirectory(_configRoot);
        Environment.SetEnvironmentVariable("CONFIG_PATH", _configRoot);

        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={DavDatabaseContext.DatabaseFilePath}")
            .AddInterceptors(new SqliteForeignKeyEnabler())
            .ReplaceService<
                IMigrationsSqlGenerator,
                SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;
        _context = new DavDatabaseContext(options);
        await _context.Database.MigrateAsync();
        _dbClient = new DavDatabaseClient(_context);
        _config = new ConfigManager();
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
        try { Directory.Delete(_configRoot, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Fact]
    public async Task Attach_Donates_WhenNameAndSegmentationMatch()
    {
        await SeedCompletedSiblingAsync(
            "older.nzb",
            DateTime.UtcNow.AddHours(-2),
            FileXml("movie.mkv",
                Seg(100, 1, "b1@example", "b1-alt@example"),
                Seg(200, 2, "b2@example")));

        var primary = MovieFile(
            ("a1@example", 100, ["a1-existing@example"]),
            ("a2@example", 200, []));

        await SiblingDonorAttacher.AttachToNewImportAsync(
            _dbClient, NewQueueItem(), [primary], _config, CancellationToken.None);

        Assert.Equal(["a1-existing@example", "b1@example", "b1-alt@example"], primary.Segments[0].FallbackMessageIds);
        Assert.Equal(["b2@example"], primary.Segments[1].FallbackMessageIds);
    }

    [Fact]
    public async Task Attach_Skips_WhenSegmentCountsDiffer()
    {
        await SeedCompletedSiblingAsync(
            "sibling.nzb",
            DateTime.UtcNow.AddHours(-1),
            FileXml("movie.mkv", Seg(100, 1, "b1@example"), Seg(100, 2, "b2@example")));

        var primary = MovieFile(("a1@example", 100, []));
        await AttachAsync(primary);

        Assert.Empty(primary.Segments[0].FallbackMessageIds);
    }

    [Fact]
    public async Task Attach_Skips_WhenDeclaredBytesDiffer()
    {
        await SeedCompletedSiblingAsync(
            "sibling.nzb",
            DateTime.UtcNow.AddHours(-1),
            FileXml("movie.mkv", Seg(100, 1, "b1@example"), Seg(200, 2, "b2@example")));

        var primary = MovieFile(("a1@example", 100, []), ("a2@example", 201, []));
        await AttachAsync(primary);

        Assert.Empty(primary.Segments[0].FallbackMessageIds);
        Assert.Empty(primary.Segments[1].FallbackMessageIds);
    }

    [Fact]
    public async Task Attach_Skips_WhenFilenamesDifferOrObfuscated()
    {
        await SeedCompletedSiblingAsync(
            "other-name.nzb",
            DateTime.UtcNow.AddHours(-1),
            FileXml("other.mkv", Seg(100, 1, "b1@example")));
        var differentName = MovieFile(("a1@example", 100, []));
        await AttachAsync(differentName);
        Assert.Empty(differentName.Segments[0].FallbackMessageIds);

        await SeedCompletedSiblingAsync(
            "obfuscated.nzb",
            DateTime.UtcNow.AddMinutes(-30),
            """
            <?xml version="1.0" encoding="utf-8"?>
            <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
              <file subject="abc123 yEnc (1/1)">
                <groups><group>alt.binaries.test</group></groups>
                <segments>
                  <segment bytes="100" number="1">obf@example</segment>
                </segments>
              </file>
            </nzb>
            """);
        var obfuscatedPrimary = new NzbFile
        {
            Subject = "xyz789 yEnc (1/1)",
            Segments = { new NzbSegment { MessageId = "a1@example", Bytes = 100, Number = 1 } },
        };
        await AttachAsync(obfuscatedPrimary);
        Assert.Empty(obfuscatedPrimary.Segments[0].FallbackMessageIds);
    }

    [Fact]
    public async Task Attach_RespectsPerSegmentCap()
    {
        _config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.VariantsSegmentDonorsMaxPerSegment, ConfigValue = "2" },
        ]);
        await SeedCompletedSiblingAsync(
            "sibling.nzb",
            DateTime.UtcNow.AddHours(-1),
            FileXml("movie.mkv",
                Seg(100, 1, "b1@example", "b1-alt1@example", "b1-alt2@example")));

        var primary = MovieFile(("a1@example", 100, ["a1-existing@example"]));
        await AttachAsync(primary);

        Assert.Equal(["a1-existing@example", "b1@example"], primary.Segments[0].FallbackMessageIds);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, GroupKey)]
    public async Task Attach_NoOp_WhenContentGroupKeyNullOrDisabled(bool enabled, string? groupKey)
    {
        _config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.VariantsSegmentDonorsEnabled,
                ConfigValue = enabled ? "true" : "false",
            },
        ]);
        await SeedCompletedSiblingAsync(
            "sibling.nzb",
            DateTime.UtcNow.AddHours(-1),
            FileXml("movie.mkv", Seg(100, 1, "b1@example")));

        var primary = MovieFile(("a1@example", 100, []));
        var queueItem = NewQueueItem();
        queueItem.ContentGroupKey = groupKey;
        await SiblingDonorAttacher.AttachToNewImportAsync(
            _dbClient, queueItem, [primary], _config, CancellationToken.None);

        Assert.Empty(primary.Segments[0].FallbackMessageIds);
    }

    [Fact]
    public async Task Attach_PrefersNewestSiblingFirst()
    {
        _config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.VariantsSegmentDonorsMaxSiblings, ConfigValue = "1" },
        ]);
        await SeedCompletedSiblingAsync(
            "older.nzb",
            DateTime.UtcNow.AddHours(-2),
            FileXml("movie.mkv", Seg(100, 1, "old@example")));
        await SeedCompletedSiblingAsync(
            "newer.nzb",
            DateTime.UtcNow.AddHours(-1),
            FileXml("movie.mkv", Seg(100, 1, "new@example")));

        var primary = MovieFile(("a1@example", 100, []));
        await AttachAsync(primary);

        Assert.Equal(["new@example"], primary.Segments[0].FallbackMessageIds);
        Assert.DoesNotContain("old@example", primary.Segments[0].FallbackMessageIds);
    }

    [Fact]
    public async Task Attach_SkipsFailedBloblessAndZeroContributionSiblings()
    {
        _config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.VariantsSegmentDonorsMaxSiblings, ConfigValue = "1" },
        ]);

        await SeedHistoryAsync(
            "failed.nzb",
            DateTime.UtcNow.AddMinutes(-10),
            HistoryItem.DownloadStatusOption.Failed,
            FileXml("movie.mkv", Seg(100, 1, "failed@example")));
        await SeedHistoryAsync(
            "blobless.nzb",
            DateTime.UtcNow.AddMinutes(-9),
            HistoryItem.DownloadStatusOption.Completed,
            nzbXml: null);
        await SeedCompletedSiblingAsync(
            "identical.nzb",
            DateTime.UtcNow.AddMinutes(-5),
            FileXml("movie.mkv", Seg(100, 1, "a1@example")));
        await SeedCompletedSiblingAsync(
            "useful.nzb",
            DateTime.UtcNow.AddHours(-2),
            FileXml("movie.mkv", Seg(100, 1, "useful@example")));

        var primary = MovieFile(("a1@example", 100, []));
        await AttachAsync(primary);

        Assert.Equal(["useful@example"], primary.Segments[0].FallbackMessageIds);
        Assert.DoesNotContain("failed@example", primary.Segments[0].FallbackMessageIds);
    }

    [Fact]
    public async Task Backfill_SwapsBlobIdAndMergesPrimariesOnly()
    {
        var sibling = await SeedSiblingWithDavFileAsync(
            "sibling.nzb",
            FileXml("movie.mkv", Seg(100, 1, "s1@example"), Seg(200, 2, "s2@example")),
            ["s1@example", "s2@example"],
            extra =>
            {
                extra.SegmentByteRanges =
                [
                    LongRange.FromStartAndSize(0, 80),
                    LongRange.FromStartAndSize(80, 160),
                ];
                extra.MissingSegmentIndices = [1];
                extra.ContainerClass = 2;
                extra.CriticalHeadEndExclusive = 42;
            });

        var incoming = MovieFile(
            ("n1@example", 100, ["contaminated@example"]),
            ("n2@example", 200, []));
        var document = NewDocument(incoming);

        await SiblingDonorAttacher.BackfillCompletedSiblingsAsync(
            _dbClient, NewQueueItem(), document, _config, CancellationToken.None);
        await _context.SaveChangesAsync();

        var reloaded = await _context.Items.AsNoTracking().SingleAsync(d => d.Id == sibling.DavItemId);
        Assert.NotNull(reloaded.FileBlobId);
        Assert.NotEqual(sibling.FileBlobId, reloaded.FileBlobId);
        Assert.Contains(await _context.BlobCleanupItems.AsNoTracking().ToListAsync(),
            x => x.Id == sibling.FileBlobId);

        var updated = await BlobStore.ReadBlob<DavNzbFile>(reloaded.FileBlobId!.Value);
        Assert.NotNull(updated);
        Assert.Equal(reloaded.FileBlobId, updated.Id);
        Assert.Equal(["s1@example", "s2@example"], updated.SegmentIds);
        Assert.Equal(["n1@example"], updated.SegmentFallbackIds![0]);
        Assert.Equal(["n2@example"], updated.SegmentFallbackIds[1]);
        Assert.DoesNotContain("contaminated@example", updated.SegmentFallbackIds.SelectMany(x => x));
        Assert.Equal(sibling.Original.SegmentByteRanges, updated.SegmentByteRanges);
        Assert.Equal(sibling.Original.MissingSegmentIndices, updated.MissingSegmentIndices);
        Assert.Equal(sibling.Original.ContainerClass, updated.ContainerClass);
        Assert.Equal(sibling.Original.CriticalHeadEndExclusive, updated.CriticalHeadEndExclusive);
    }

    [Fact]
    public async Task Backfill_DoesNotMutateCachedInstance()
    {
        var sibling = await SeedSiblingWithDavFileAsync(
            "sibling.nzb",
            FileXml("movie.mkv", Seg(100, 1, "s1@example")),
            ["s1@example"]);

        var cached = await BlobStore.ReadBlob<DavNzbFile>(sibling.FileBlobId);
        Assert.NotNull(cached);
        var cachedFallbacks = cached.SegmentFallbackIds;

        var incoming = MovieFile(("n1@example", 100, []));
        await SiblingDonorAttacher.BackfillCompletedSiblingsAsync(
            _dbClient, NewQueueItem(), NewDocument(incoming), _config, CancellationToken.None);
        await _context.SaveChangesAsync();

        Assert.Same(cachedFallbacks, cached.SegmentFallbackIds);
        Assert.True(cached.SegmentFallbackIds is null or { Length: 0 }
                    || cached.SegmentFallbackIds.All(x => x is null || x.Length == 0));
    }

    [Fact]
    public async Task Backfill_SkipsBlobSwap_WhenNoNewIds()
    {
        var sibling = await SeedSiblingWithDavFileAsync(
            "sibling.nzb",
            FileXml("movie.mkv", Seg(100, 1, "s1@example")),
            ["s1@example"]);

        var incoming = MovieFile(("s1@example", 100, []));
        await SiblingDonorAttacher.BackfillCompletedSiblingsAsync(
            _dbClient, NewQueueItem(), NewDocument(incoming), _config, CancellationToken.None);
        await _context.SaveChangesAsync();

        var reloaded = await _context.Items.AsNoTracking().SingleAsync(d => d.Id == sibling.DavItemId);
        Assert.Equal(sibling.FileBlobId, reloaded.FileBlobId);
        Assert.Empty(await _context.BlobCleanupItems.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Backfill_LeavesSiblingUntouched_WhenGatesFail()
    {
        var sibling = await SeedSiblingWithDavFileAsync(
            "sibling.nzb",
            FileXml("other.mkv", Seg(100, 1, "s1@example")),
            ["s1@example"]);

        var incoming = MovieFile(("n1@example", 100, []));
        await SiblingDonorAttacher.BackfillCompletedSiblingsAsync(
            _dbClient, NewQueueItem(), NewDocument(incoming), _config, CancellationToken.None);
        await _context.SaveChangesAsync();

        var reloaded = await _context.Items.AsNoTracking().SingleAsync(d => d.Id == sibling.DavItemId);
        Assert.Equal(sibling.FileBlobId, reloaded.FileBlobId);
    }

    [Fact]
    public async Task Backfill_MergesSiblingIdsIntoPendingNewBlobs()
    {
        await SeedSiblingWithDavFileAsync(
            "sibling.nzb",
            FileXml("movie.mkv", Seg(100, 1, "s1@example", "s1-alt@example")),
            ["s1@example"]);

        var incoming = MovieFile(("n1@example", 100, []));
        var pending = new DavNzbFile
        {
            Id = Guid.NewGuid(),
            SegmentIds = incoming.GetSegmentIds(),
            SegmentFallbackIds = [[]],
        };
        _context.AddBlob(pending);

        await SiblingDonorAttacher.BackfillCompletedSiblingsAsync(
            _dbClient, NewQueueItem(), NewDocument(incoming), _config, CancellationToken.None);

        Assert.Equal(["s1@example", "s1-alt@example"], pending.SegmentFallbackIds![0]);
        Assert.Contains(_context.BlobNzbFiles, blob => blob.Id == pending.Id);
    }

    private Task AttachAsync(NzbFile primary) =>
        SiblingDonorAttacher.AttachToNewImportAsync(
            _dbClient, NewQueueItem(), [primary], _config, CancellationToken.None);

    private static QueueItem NewQueueItem() => new()
    {
        Id = Guid.NewGuid(),
        CreatedAt = DateTime.UtcNow,
        FileName = "new.nzb",
        JobName = "new",
        Category = "movies",
        ContentGroupKey = GroupKey,
    };

    private static NzbFile MovieFile(params (string id, long bytes, string[] fallbacks)[] segments)
    {
        var file = new NzbFile { Subject = """[1/1] - "movie.mkv" yEnc 12345 (1/1)""" };
        for (var i = 0; i < segments.Length; i++)
        {
            file.Segments.Add(new NzbSegment
            {
                MessageId = segments[i].id,
                Bytes = segments[i].bytes,
                Number = i + 1,
                FallbackMessageIds = segments[i].fallbacks,
            });
        }

        return file;
    }

    private static NzbDocument NewDocument(NzbFile file)
    {
        var document = new NzbDocument();
        document.Files.Add(file);
        return document;
    }

    private Task SeedCompletedSiblingAsync(string fileName, DateTime createdAt, string nzbXml) =>
        SeedHistoryAsync(fileName, createdAt, HistoryItem.DownloadStatusOption.Completed, nzbXml);

    private async Task<(Guid HistoryId, Guid DavItemId, Guid FileBlobId, DavNzbFile Original)> SeedSiblingWithDavFileAsync(
        string fileName,
        string nzbXml,
        string[] segmentIds,
        Action<DavNzbFile>? configure = null)
    {
        var historyId = await SeedHistoryAsync(
            fileName, DateTime.UtcNow.AddHours(-1), HistoryItem.DownloadStatusOption.Completed, nzbXml);

        var fileBlobId = Guid.NewGuid();
        var davNzbFile = new DavNzbFile
        {
            Id = fileBlobId,
            SegmentIds = segmentIds,
        };
        configure?.Invoke(davNzbFile);
        await BlobStore.WriteBlob(fileBlobId, davNzbFile);

        var davItem = DavItem.New(
            Guid.NewGuid(),
            DavItem.ContentFolder,
            fileName,
            fileSize: 100,
            DavItem.ItemType.UsenetFile,
            DavItem.ItemSubType.NzbFile,
            releaseDate: DateTimeOffset.UtcNow.AddDays(-1),
            lastHealthCheck: null,
            historyItemId: historyId,
            fileBlobId: fileBlobId);
        _context.Items.Add(davItem);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return (historyId, davItem.Id, fileBlobId, davNzbFile);
    }

    private async Task<Guid> SeedHistoryAsync(
        string fileName,
        DateTime createdAt,
        HistoryItem.DownloadStatusOption status,
        string? nzbXml)
    {
        var id = Guid.NewGuid();
        if (nzbXml is not null)
        {
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(nzbXml));
            await BlobStore.WriteBlob(id, stream);
        }

        _context.HistoryItems.Add(new HistoryItem
        {
            Id = id,
            CreatedAt = createdAt,
            FileName = fileName,
            JobName = Path.GetFileNameWithoutExtension(fileName),
            Category = "movies",
            DownloadStatus = status,
            TotalSegmentBytes = 100,
            DownloadTimeSeconds = 1,
            NzbBlobId = nzbXml is null ? null : id,
            ContentGroupKey = GroupKey,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return id;
    }

    private static string FileXml(string fileName, params string[] segments) =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">
          <file subject="[1/1] - &quot;{fileName}&quot; yEnc 12345 (1/1)">
            <groups><group>alt.binaries.test</group></groups>
            <segments>
              {string.Join("\n              ", segments)}
            </segments>
          </file>
        </nzb>
        """;

    private static string Seg(long bytes, int number, string primary, params string[] fallbacks)
    {
        var parts = new List<string>
        {
            $"<segment bytes=\"{bytes}\" number=\"{number}\">{primary}</segment>",
        };
        parts.AddRange(fallbacks.Select(
            id => $"<segment bytes=\"{bytes}\" number=\"{number}\">{id}</segment>"));
        return string.Join("\n              ", parts);
    }
}
