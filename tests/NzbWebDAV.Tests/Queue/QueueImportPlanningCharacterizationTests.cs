using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.Database;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Websocket;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using SharpCompress.Common;
using SharpCompress.Writers.SevenZip;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Queue;

/// <summary>
/// Characterizes the queue import planning boundary end to end: which stages run in
/// which order, what gets mounted, and how failures are classified (completed, failed
/// into history, or left queued for retry). Each case pins the full observable
/// outcome so the planning pipeline can be restructured without behavioural drift.
/// </summary>
[Collection(nameof(ConfigPathCollection))]
public sealed class QueueImportPlanningCharacterizationTests : IAsyncLifetime
{
    private const string Category = "other";
    private const int VolumeSize = 4096;
    private static readonly byte[] MatroskaMagic = [0x1A, 0x45, 0xDF, 0xA3];

    // Stored (copy) 7z with dir/before.txt, dir/data.bin and dir/after.txt; same fixture as SevenZipProcessorTests.
    private const string StoredSevenZipBase64 =
        "N3q8ryccAARu4/hlFQAAAAAAAACEAAAAAAAAACp/hIpzdG9yZWQtc2V2ZW56aXAtZW50cnkBBAYAAQkVAAcLAQABAQAMFQAICgHuw9/ZAAAFBA4B0A8BYBFdAGQAaQByAAAAZABpAHIALwBiAGUAZgBvAHIAZQAuAHQAeAB0AAAAZABpAHIALwBkAGEAdABhAC4AYgBpAG4AAABkAGkAcgAvAGEAZgB0AGUAcgAuAHQAeAB0AAAAAAA=";

    private readonly string _root = Path.Join(Path.GetTempPath(), "queue-planning-" + Guid.NewGuid().ToString("N"));
    private readonly string _run = Guid.NewGuid().ToString("N")[..12];
    private string? _previous;

    public async Task InitializeAsync()
    {
        _previous = Environment.GetEnvironmentVariable("CONFIG_PATH");
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("CONFIG_PATH", _root);
        DavDatabaseContext.ResetOptionsForTests();
        await using var context = new DavDatabaseContext();
        await context.Database.MigrateAsync();
    }

    public Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previous);
        DavDatabaseContext.ResetOptionsForTests();
        Directory.Delete(_root, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task DirectMedia()
    {
        var snapshot = await RunAsync("direct-media",
        [
            new("movie.mkv", Media(2 * VolumeSize)),
            new("movie.nfo", Encoding.ASCII.GetBytes("release notes")),
        ]);
        Assert.Equal(Expected.DirectMedia, snapshot);
    }

    [Fact]
    public async Task SplitVideoParts()
    {
        var media = Media(2 * VolumeSize);
        var snapshot = await RunAsync("split-video",
        [
            new("movie.mkv.001", media[..VolumeSize]),
            new("movie.mkv.002", media[VolumeSize..]),
        ]);
        Assert.Equal(Expected.SplitVideoParts, snapshot);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SingleRarSet(bool lazyRar)
    {
        var snapshot = await RunAsync(lazyRar ? "single-rar-lazy" : "single-rar-eager",
            RarSet("movie", "movie.mkv"), lazyRar);
        Assert.Equal(lazyRar ? Expected.SingleRarSetLazy : Expected.SingleRarSetEager, snapshot);
    }

    [Fact]
    public async Task MultipleArchiveSets()
    {
        var snapshot = await RunAsync("multi-set",
        [
            .. RarSet("Show.S01E01", "Show.S01E01.mkv"),
            .. RarSet("Show.S01E02", "Show.S01E02.mkv"),
        ]);
        Assert.Equal(Expected.MultipleArchiveSets, snapshot);
    }

    [Fact]
    public async Task NestedArchive()
    {
        var inner = Rar4TestArchiveBuilder.BuildRar4Volume(
            "movie.mkv", VolumeSize, payloadPrefix: MatroskaMagic, isVolume: false, endOfArchive: true);
        var outer = Rar4TestArchiveBuilder.BuildRar4Volume(
            "inner.rar", inner.Length, payloadPrefix: inner, isVolume: false, endOfArchive: true);
        var snapshot = await RunAsync("nested", [new("outer.rar", outer)]);
        Assert.Equal(Expected.NestedArchive, snapshot);
    }

    [Fact]
    public async Task PasswordedArchive()
    {
        var volume = Rar4TestArchiveBuilder.BuildRar4Volume(
            "movie.mkv", VolumeSize, firstVolume: true, payloadPrefix: MatroskaMagic, encrypted: true);
        var snapshot = await RunAsync("passworded", [new("movie.rar", volume)]);
        Assert.Equal(Expected.PasswordedArchive, snapshot);
    }

    [Fact]
    public async Task StoredSevenZipArchive()
    {
        var snapshot = await RunAsync("sevenzip-stored",
            [new("movie.7z", Convert.FromBase64String(StoredSevenZipBase64))]);
        Assert.Equal(Expected.StoredSevenZipArchive, snapshot);
    }

    [Fact]
    public async Task CompressedSevenZipArchive()
    {
        using var archive = new MemoryStream();
        using (var writer = new SevenZipWriter(archive, new SevenZipWriterOptions(CompressionType.LZMA2)
        {
            LeaveStreamOpen = true,
            CompressHeader = false,
            Solid = false,
        }))
        {
            using var payload = new MemoryStream(Media(VolumeSize));
            writer.Write("movie.mkv", payload, DateTime.UnixEpoch);
        }

        var snapshot = await RunAsync("sevenzip-compressed", [new("movie.7z", archive.ToArray())]);
        Assert.Equal(Expected.CompressedSevenZipArchive, snapshot);
    }

    [Fact]
    public async Task MissingFirstSegment()
    {
        var snapshot = await RunAsync("missing-first", [new("movie.mkv", Media(VolumeSize), Served: false)]);
        Assert.Equal(Expected.MissingFirstSegment, snapshot);
    }

    [Fact]
    public async Task MalformedArchive()
    {
        var garbage = new byte[VolumeSize];
        Encoding.ASCII.GetBytes("this is not a rar archive").CopyTo(garbage, 0);
        var snapshot = await RunAsync("malformed", [new("movie.rar", garbage)]);
        Assert.Equal(Expected.MalformedArchive, snapshot);
    }

    [Fact]
    public async Task TransientProviderFailure()
    {
        // The first-segment probe of part2 succeeds; the later RAR header read times out.
        var snapshot = await RunAsync("transient", RarSet("movie", "movie.mkv"), lazyRar: false,
            faultOnFetch: (segmentId, fetch) => segmentId.Contains(".part2.rar", StringComparison.Ordinal) && fetch > 1);
        Assert.Equal(Expected.TransientProviderFailure, snapshot);
    }

    private static byte[] Media(int length)
    {
        var bytes = Enumerable.Range(0, length).Select(index => (byte)(index % 251)).ToArray();
        MatroskaMagic.CopyTo(bytes, 0);
        return bytes;
    }

    private static List<PostedFile> RarSet(string baseName, string memberName) =>
    [
        new($"{baseName}.part1.rar", Rar4TestArchiveBuilder.BuildRar4SplitFirstVolume(
            memberName, VolumeSize, 2 * VolumeSize, MatroskaMagic)),
        new($"{baseName}.part2.rar", Rar4TestArchiveBuilder.BuildRar4ContinuationVolume(
            memberName, VolumeSize, uncompressedSize: 2 * VolumeSize)),
    ];

    private async Task<string> RunAsync(
        string jobName,
        IReadOnlyList<PostedFile> files,
        bool lazyRar = true,
        Func<string, int, bool>? faultOnFetch = null)
    {
        var fetches = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        Func<string, byte[], Stream>? decodedStreamFactory = faultOnFetch is null
            ? null
            : (segmentId, bytes) =>
            {
                var fetch = fetches.AddOrUpdate(segmentId, 1, (_, previous) => previous + 1);
                if (faultOnFetch(segmentId, fetch))
                    throw new TimeoutException("simulated provider timeout");
                return new MemoryStream(bytes, writable: false);
            };
        var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var headers = new Dictionary<string, UsenetYencHeader>(StringComparer.Ordinal);
        var nzbFiles = new List<XElement>();
        foreach (var file in files)
        {
            var segmentId = $"{jobName}-{file.Name}-{_run}@example.invalid";
            if (file.Served)
            {
                payloads[segmentId] = file.Bytes;
                headers[segmentId] = new UsenetYencHeader
                {
                    FileName = file.Name,
                    FileSize = file.Bytes.Length,
                    LineLength = 128,
                    PartNumber = 1,
                    TotalParts = 1,
                    HasTotalParts = true,
                    PartOffset = 0,
                    PartSize = file.Bytes.Length,
                };
            }

            nzbFiles.Add(new XElement("file",
                new XAttribute("subject", $"\"{file.Name}\" yEnc (1/1)"),
                new XElement("segments",
                    new XElement("segment",
                        new XAttribute("bytes", file.Bytes.Length),
                        new XAttribute("number", 1),
                        segmentId))));
        }

        var nzb = Encoding.UTF8.GetBytes(new XElement("nzb", nzbFiles).ToString(SaveOptions.DisableFormatting));
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.ApiRenameSingleVideoToRelease, ConfigValue = "false" },
            new ConfigItem { ConfigName = ConfigKeys.ApiEnsureArticleExistenceCategories, ConfigValue = Category },
            new ConfigItem { ConfigName = ConfigKeys.ApiArticleExistenceCheckMode, ConfigValue = "full" },
            new ConfigItem { ConfigName = ConfigKeys.ApiLazyRarParsing, ConfigValue = lazyRar ? "true" : "false" },
        ]);

        await using var context = new DavDatabaseContext();
        var queueItem = new QueueItem
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            FileName = $"{jobName}.nzb",
            JobName = jobName,
            NzbFileSize = nzb.Length,
            TotalSegmentBytes = files.Sum(file => (long)file.Bytes.Length),
            Category = Category,
            Priority = QueueItem.PriorityOption.Normal,
            PostProcessing = QueueItem.PostProcessingOption.None,
        };
        context.QueueItems.Add(queueItem);
        await context.SaveChangesAsync();

        var stages = new List<string>();
        var sink = new JobLogSink(jobName, queueItem.Id);
        var previousLogger = Log.Logger;
        using (var logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger())
        {
            Log.Logger = logger;
            try
            {
                var fake = new FakeNntpClient(
                    payloads,
                    useCachedYencStreams: true,
                    decodedStreamFactory: decodedStreamFactory,
                    yencHeaders: headers);
                using var client = new DatedArticleClient(fake);
                using var gate = new HealthCheckConnectionGate(config);
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await using var nzbStream = new MemoryStream(nzb);
                var processor = new QueueItemProcessor(
                    queueItem,
                    nzbStream,
                    new DavDatabaseClient(context),
                    client,
                    config,
                    new WebsocketManager(),
                    new ProviderUsageTracker(),
                    new WatchdogLog(),
                    new QueueItemSourceTracker(),
                    new Progress<int>(),
                    new ConcurrentDictionary<Guid, int>(),
                    finalizeLock: null,
                    gate,
                    cancellation.Token,
                    stage =>
                    {
                        lock (stages) stages.Add(stage);
                    });
                await processor.ProcessAsync();
            }
            finally
            {
                Log.Logger = previousLogger;
            }
        }

        return await SnapshotAsync(context, queueItem, stages, sink);
    }

    private async Task<string> SnapshotAsync(
        DavDatabaseContext context,
        QueueItem queueItem,
        List<string> stages,
        JobLogSink sink)
    {
        context.ChangeTracker.Clear();
        var text = new StringBuilder();
        text.Append("stages: ").AppendJoin(" > ", stages).Append('\n');

        var queued = await context.QueueItems.AsNoTracking().Where(item => item.Id == queueItem.Id).ToListAsync();
        text.Append(CultureInfo.InvariantCulture,
            $"queued: {queued.Count} paused: {queued.Any(item => item.PauseUntil != null)}\n");

        foreach (var history in await context.HistoryItems.AsNoTracking()
                     .Where(item => item.Id == queueItem.Id).ToListAsync())
        {
            text.Append(CultureInfo.InvariantCulture,
                $"history: {history.DownloadStatus} fail: {history.FailMessage ?? "-"}\n");
        }

        var mountPath = $"{DavItem.ContentFolder.Path.TrimEnd('/')}/{Category}/{queueItem.JobName}";
        var mounted = await context.Items.AsNoTracking()
            .Where(item => item.Path.StartsWith(mountPath + "/"))
            .ToListAsync();
        foreach (var item in mounted.OrderBy(item => item.Path, StringComparer.Ordinal))
        {
            text.Append(CultureInfo.InvariantCulture,
                $"item: {item.Path[(mountPath.Length + 1)..]} {item.Type}/{item.SubType} {item.FileSize?.ToString(CultureInfo.InvariantCulture) ?? "-"}\n");
        }

        foreach (var entry in sink.Entries)
            text.Append("log: ").Append(entry).Append('\n');

        return text.ToString().Replace(_run, "<run>", StringComparison.Ordinal);
    }

    private sealed record PostedFile(string Name, byte[] Bytes, bool Served = true);

    /// <summary>Warning-and-above events that carry this job's identity.</summary>
    private sealed class JobLogSink(string jobName, Guid queueItemId) : ILogEventSink
    {
        private readonly ConcurrentQueue<string> _entries = new();

        public IEnumerable<string> Entries => _entries;


        public void Emit(LogEvent logEvent)
        {
            if (logEvent.Level < LogEventLevel.Warning) return;
            var mine = logEvent.Properties.Any(property =>
                property.Key is "JobName" or "QueueItemId" or "NzoId" &&
                property.Value is ScalarValue { Value: { } value } &&
                (Equals(value.ToString(), jobName) || Equals(value.ToString(), queueItemId.ToString())));
            if (!mine) return;
            _entries.Enqueue(
                $"{logEvent.Level}: {logEvent.MessageTemplate.Text} exception={logEvent.Exception?.GetType().Name ?? "-"}");
        }
    }

    /// <summary>First-segment fetches read whole articles for their Date header.</summary>
    private sealed class DatedArticleClient(INntpClient inner) : WrappingNntpClient(inner)
    {
        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            DecodedArticleAsync(segmentId, null, cancellationToken);

        public override async Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken)
        {
            var body = await InnerClient.DecodedBodyAsync(segmentId, onConnectionReadyAgain, cancellationToken);
            Assert.NotNull(body.Stream);
            return new UsenetDecodedArticleResponse
            {
                SegmentId = body.SegmentId,
                ResponseCode = 220,
                ResponseMessage = "220 test article",
                Stream = body.Stream,
                ArticleHeaders = new UsenetArticleHeader
                {
                    Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Date"] = DateTimeOffset.UnixEpoch.ToString("R", CultureInfo.InvariantCulture),
                    },
                },
            };
        }
    }

    private static class Expected
    {
        public const string DirectMedia =
            "stages: sibling-donors > first-segment > par2 > processors > health > import-readiness > finalize-commit\n" +
            "queued: 0 paused: False\n" +
            "history: Completed fail: -\n" +
            "item: movie.mkv UsenetFile/NzbFile 8192\n";

        public const string SplitVideoParts =
            "stages: sibling-donors > first-segment > par2 > processors > health > import-readiness > finalize-commit\n" +
            "queued: 0 paused: False\n" +
            "history: Completed fail: -\n" +
            "item: movie.mkv UsenetFile/MultipartFile 8192\n";

        public const string SingleRarSetLazy =
            "stages: sibling-donors > first-segment > par2 > lazy-rar > processors > health > import-readiness > finalize-commit\n" +
            "queued: 0 paused: False\n" +
            "history: Completed fail: -\n" +
            "item: movie.mkv UsenetFile/MultipartFile 8192\n";

        public const string SingleRarSetEager =
            "stages: sibling-donors > first-segment > par2 > processors > health > import-readiness > finalize-commit\n" +
            "queued: 0 paused: False\n" +
            "history: Completed fail: -\n" +
            "item: movie.mkv UsenetFile/MultipartFile 8192\n";

        public const string MultipleArchiveSets =
            "stages: sibling-donors > first-segment > par2 > lazy-rar > processors > health > import-readiness > finalize-commit\n" +
            "queued: 0 paused: False\n" +
            "history: Completed fail: -\n" +
            "item: Show.S01E01.mkv UsenetFile/MultipartFile 8192\n" +
            "item: Show.S01E02.mkv UsenetFile/MultipartFile 8192\n";

        public const string NestedArchive =
            "stages: sibling-donors > first-segment > par2 > lazy-rar > processors > health > import-readiness > finalize-commit\n" +
            "queued: 0 paused: False\n" +
            "history: Completed fail: -\n" +
            "item: movie.mkv UsenetFile/MultipartFile 4096\n";

        public const string PasswordedArchive =
            "stages: sibling-donors > first-segment > par2 > lazy-rar > processors > health > import-readiness > finalize-commit\n" +
            "queued: 0 paused: False\n" +
            "history: Completed fail: -\n" +
            "item: movie.mkv UsenetFile/MultipartFile 4096\n";

        public const string StoredSevenZipArchive =
            "stages: sibling-donors > first-segment > par2 > processors > health > import-readiness > finalize-commit\n" +
            "queued: 0 paused: False\n" +
            "history: Completed fail: -\n" +
            "item: dir Directory/Directory -\n" +
            "item: dir/after.txt UsenetFile/MultipartFile 0\n" +
            "item: dir/before.txt UsenetFile/MultipartFile 0\n" +
            "item: dir/data.bin UsenetFile/MultipartFile 21\n";

        public const string CompressedSevenZipArchive =
            "stages: sibling-donors > first-segment > par2 > processors > finalize-commit\n" +
            "queued: 0 paused: False\n" +
            "history: Failed fail: Compressed 7z archives are intentionally unsupported by InfiniDysk: direct streaming and seeking require uncompressed (Copy/store) archive contents. This is a design limitation, not an application error. Choose a different release with uncompressed archives or direct media files.\n" +
            "log: Error: Failed queue item {JobName} ({QueueItemId}) after {ElapsedSeconds} seconds: {Reason} exception=-\n";

        public const string MissingFirstSegment =
            "stages: sibling-donors > first-segment > finalize-commit\n" +
            "queued: 0 paused: False\n" +
            "history: Failed fail: Missing articles: 1 important file(s) have missing segments across all providers (e.g. movie.mkv). NZB is likely DMCA'd or expired.\n" +
            "log: Error: Failed queue item {JobName} ({QueueItemId}) after {ElapsedSeconds} seconds: {Reason} exception=-\n";

        public const string MalformedArchive =
            "stages: sibling-donors > first-segment > par2 > lazy-rar > processors > finalize-commit\n" +
            "queued: 0 paused: False\n" +
            "history: Failed fail: Failed to parse RAR volume headers: Rar signature not found\n" +
            "log: Error: Failed queue item {JobName} ({QueueItemId}) after {ElapsedSeconds} seconds: {Reason} exception=-\n";

        public const string TransientProviderFailure =
            "stages: sibling-donors > first-segment > par2 > processors\n" +
            "queued: 1 paused: True\n" +
            "log: Warning: Provider connection issue for queue item {JobName} (attempt {Attempt}/{MaxAttempts}); retrying in {BackoffSeconds:0} seconds. Reason: {Reason} exception=-\n";
    }
}
