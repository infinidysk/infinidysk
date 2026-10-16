using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Database;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Websocket;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Queue;

[Collection(nameof(ConfigPathCollection))]
public sealed class OmittedSegmentImportTests : IAsyncLifetime
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "omitted-import-" + Guid.NewGuid().ToString("N"));
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
    public Task OmittedSegment_ImportsWithTrustedLayoutAndPendingHealth() => ImportAsync(true);

    [Fact]
    public Task SparseLabels_CompletePostStillImports() => ImportAsync(false);

    private async Task ImportAsync(bool omitted)
    {
        const int partSize = 4096;
        var totalParts = omitted ? 4 : 2;
        var bytes = Enumerable.Range(0, partSize * totalParts).Select(index => (byte)(index % 251)).ToArray();
        new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }.CopyTo(bytes, 0);
        var payloads = new Dictionary<string, byte[]>();
        var headers = new Dictionary<string, UsenetYencHeader>();
        var ranges = new Dictionary<string, LongRange>();
        var entries = new XElement("segments");
        for (var part = 1; part <= totalParts; part++)
        {
            if (omitted && part == 3) continue;
            var number = !omitted && part == 2 ? 3 : part;
            var id = $"part-{number}@example";
            var offset = (part - 1) * partSize;
            payloads[id] = bytes.AsSpan(offset, partSize).ToArray();
            ranges[id] = new LongRange(offset, offset + partSize);
            headers[id] = new UsenetYencHeader
            {
                FileName = "movie.mkv", FileSize = bytes.Length, LineLength = 128,
                PartNumber = part, TotalParts = totalParts, HasTotalParts = true,
                PartOffset = offset, PartSize = partSize,
            };
            entries.Add(new XElement("segment", new XAttribute("bytes", partSize), new XAttribute("number", number), id));
        }
        var document = new XElement("nzb", new XElement("file", new XAttribute("subject", "\"movie.mkv\" yEnc"), entries));
        var nzb = Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting));
        var config = new ConfigManager();
        config.UpdateValues([
            new ConfigItem { ConfigName = ConfigKeys.ApiRenameSingleVideoToRelease, ConfigValue = "false" },
            new ConfigItem { ConfigName = ConfigKeys.ApiEnsureArticleExistenceCategories, ConfigValue = "other" },
            new ConfigItem { ConfigName = ConfigKeys.ApiArticleExistenceCheckMode, ConfigValue = "full" },
        ]);
        Assert.Contains("other", config.GetMediaReadinessCategories());
        await using var context = new DavDatabaseContext();
        var queueItem = new QueueItem
        {
            Id = Guid.NewGuid(), CreatedAt = DateTime.UtcNow, FileName = "omitted.nzb", JobName = "omitted-import",
            NzbFileSize = nzb.Length, TotalSegmentBytes = payloads.Values.Sum(payload => payload.Length),
            Category = "other", Priority = QueueItem.PriorityOption.Normal, PostProcessing = QueueItem.PostProcessingOption.None,
        };
        context.QueueItems.Add(queueItem);
        await context.SaveChangesAsync();
        var store = new RepairPatchStore(Path.Join(_root, "patches"), 1024 * 1024);
        await store.EnsureCatalogLoadedAsync(CancellationToken.None);
        var fake = new FakeNntpClient(payloads, useCachedYencStreams: true, segmentRanges: ranges, yencHeaders: headers);
        using var client = new RepairedSegmentNntpClient(new ImportArticleClient(fake), store);
        using var gate = new HealthCheckConnectionGate(config);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var nzbStream = new MemoryStream(nzb);
        var processor = new QueueItemProcessor(queueItem, nzbStream, new DavDatabaseClient(context),
            client, config, new WebsocketManager(), new Progress<int>(), gate, cancellation.Token);
        await processor.ProcessAsync();

        context.ChangeTracker.Clear();
        var history = Assert.Single(await context.HistoryItems.AsNoTracking().ToListAsync());
        Assert.Null(history.FailMessage);
        Assert.Equal(HistoryItem.DownloadStatusOption.Completed, history.DownloadStatus);
        Assert.Empty(await context.QueueItems.AsNoTracking().ToListAsync());
        var item = Assert.Single(await context.Items.AsNoTracking()
            .Where(item => item.Name == "movie.mkv" && item.SubType == DavItem.ItemSubType.NzbFile).ToListAsync());
        Assert.Equal(bytes.Length, item.FileSize);
        if (omitted) Assert.Null(item.LastHealthCheck);
        else Assert.NotNull(item.LastHealthCheck);
        var blob = await BlobStore.ReadBlob<DavNzbFile>(item.FileBlobId!.Value);
        Assert.NotNull(blob);
        Assert.Equal(totalParts, blob.SegmentIds.Length);
        Assert.True(blob.SegmentByteRangesTrusted);
        Assert.Equal(Enumerable.Range(0, totalParts).Select(index => new LongRange(index * partSize, (index + 1) * partSize)), blob.SegmentByteRanges);
        Assert.Equal(omitted ? 1 : 0, blob.SegmentIds.Count(NzbFile.IsOmittedSegmentId));
        await using var stream = new NzbFileStream(blob.SegmentIds, item.FileSize!.Value, client, 4,
            blob.SegmentByteRanges, segmentByteRangesTrusted: blob.SegmentByteRangesTrusted.GetValueOrDefault());
        await using var output = new MemoryStream();
        await stream.CopyToAsync(output, cancellation.Token);
        if (omitted) bytes.AsSpan(8192, 4096).Clear();
        Assert.Equal(bytes, output.ToArray());
        Assert.DoesNotContain(fake.RequestedSegmentIds, NzbFile.IsOmittedSegmentId);
        Assert.DoesNotContain(fake.StatRequestOrder, NzbFile.IsOmittedSegmentId);
        Assert.True(payloads.Keys.All(fake.StatRequestCounts.ContainsKey));
    }

    private sealed class ImportArticleClient(INntpClient inner) : WrappingNntpClient(inner)
    {
        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(SegmentId segmentId, CancellationToken cancellationToken) =>
            DecodedArticleAsync(segmentId, null, cancellationToken);

        public override async Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, ArticleBodyCompletionHandler? onConnectionReadyAgain, CancellationToken cancellationToken)
        {
            var body = await InnerClient.DecodedBodyAsync(segmentId, onConnectionReadyAgain, cancellationToken);
            Assert.NotNull(body.Stream);
            return new UsenetDecodedArticleResponse
            {
                SegmentId = body.SegmentId, ResponseCode = 220, ResponseMessage = "220 test article", Stream = body.Stream,
                ArticleHeaders = new UsenetArticleHeader
                {
                    Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Date"] = DateTimeOffset.UtcNow.ToString("R", CultureInfo.InvariantCulture),
                    },
                },
            };
        }
    }
}