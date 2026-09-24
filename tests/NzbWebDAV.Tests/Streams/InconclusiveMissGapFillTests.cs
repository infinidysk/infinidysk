using System.Text;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Streams;
using NzbWebDAV.Tests.Clients.Usenet;
using NzbWebDAV.Tests.Fakes;

namespace NzbWebDAV.Tests.Streams;

/// <summary>
/// Playback must not turn a miss that not every provider confirmed into durable
/// missing-article evidence (#1543).
/// </summary>
[Collection(nameof(PlaybackHoleTrackerCollection))]
public sealed class InconclusiveMissGapFillTests : IDisposable
{
    private const string OkId = "ok@test";
    private const string MissingId = "missing@test";
    private const string AlternateId = "alternate@test";

    public InconclusiveMissGapFillTests() => PlaybackHoleTracker.ResetForTests();

    public void Dispose() => PlaybackHoleTracker.ResetForTests();

    [Theory]
    [InlineData(4, false)]
    [InlineData(4, true)]
    [InlineData(0, false)]
    public async Task MidStreamMiss_WithOpenCircuitProvider_FillsGapWithoutRecordingMissingSegment(
        int articleBufferSize, bool usePipelinedBodyRequests)
    {
        var path = $"/content/inconclusive-{Guid.NewGuid():N}.mkv";
        using var client = CreateClient(firstProviderOpen: true);

        await WithPar2SinkAsync(async service =>
        {
            await using var stream = CreateStream(
                client, [OkId, MissingId], articleBufferSize, usePipelinedBodyRequests, path);
            using var output = new MemoryStream();

            await stream.CopyToAsync(output);

            Assert.Equal("abcd\0\0\0\0", Encoding.ASCII.GetString(output.ToArray()));
            Assert.False(service.HasPendingZeroFillPath(path));
        });

        Assert.False(PlaybackHoleTracker.IsKnownMissingSegment(path, MissingId));
        Assert.Null(PlaybackHoleTracker.SnapshotMissingSegmentIds(path));
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(4, true)]
    [InlineData(0, false)]
    public async Task MidStreamMiss_ConfirmedByEveryProvider_StillRecordsMissingSegment(
        int articleBufferSize, bool usePipelinedBodyRequests)
    {
        var path = $"/content/confirmed-{Guid.NewGuid():N}.mkv";
        using var client = CreateClient(firstProviderOpen: false);

        await WithPar2SinkAsync(async service =>
        {
            await using var stream = CreateStream(
                client, [OkId, MissingId], articleBufferSize, usePipelinedBodyRequests, path);
            using var output = new MemoryStream();

            await stream.CopyToAsync(output);

            Assert.Equal("abcd\0\0\0\0", Encoding.ASCII.GetString(output.ToArray()));
            Assert.True(service.HasPendingZeroFillPath(path));
        });

        Assert.True(PlaybackHoleTracker.IsKnownMissingSegment(path, MissingId));
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(4, true)]
    [InlineData(0, false)]
    public async Task ConsecutiveInconclusiveMisses_StillFailFast_WithoutRecordingMissingSegments(
        int articleBufferSize, bool usePipelinedBodyRequests)
    {
        // Every segment is missing: a good segment would let the buffered prefetch call
        // RecordGoodSegment after the first hole and race the tracker assertion.
        var path = $"/content/inconclusive-cap-{Guid.NewGuid():N}.mkv";
        var missingIds = Enumerable.Range(0, GapFillLimits.MaxConsecutiveZeroFills)
            .Select(index => $"missing-{index}@test")
            .ToArray();
        using var client = CreateClient(firstProviderOpen: true);
        await using var stream = CreateStream(
            client, missingIds, articleBufferSize, usePipelinedBodyRequests, path,
            failFastOnFirstSegment: false);

        var miss = await Assert.ThrowsAsync<UsenetArticleNotFoundException>(
            async () => await stream.CopyToAsync(Stream.Null));

        Assert.NotNull(miss.InconclusiveReason);
        Assert.True(PlaybackHoleTracker.ShouldFailFast(path, out var stored));
        Assert.NotNull(Assert.IsType<UsenetArticleNotFoundException>(stored).InconclusiveReason);
        Assert.All(missingIds, id => Assert.False(PlaybackHoleTracker.IsKnownMissingSegment(path, id)));
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(4, true)]
    [InlineData(0, false)]
    public async Task FirstSegmentMiss_WithOpenCircuitProvider_ThrowsInconclusiveMiss(
        int articleBufferSize, bool usePipelinedBodyRequests)
    {
        var path = $"/content/inconclusive-first-{Guid.NewGuid():N}.mkv";
        using var client = CreateClient(firstProviderOpen: true);
        await using var stream = CreateStream(
            client, [MissingId, OkId], articleBufferSize, usePipelinedBodyRequests, path);

        var miss = await Assert.ThrowsAsync<UsenetArticleNotFoundException>(
            async () => await stream.CopyToAsync(Stream.Null));

        Assert.Equal(MissingId, miss.SegmentId);
        Assert.Equal(MultiProviderNntpClient.InconclusiveMissReason, miss.InconclusiveReason);
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(4, true)]
    [InlineData(0, false)]
    public async Task FirstSegmentMiss_ConfirmedPrimaryWithInconclusiveAlternate_ThrowsInconclusiveMiss(
        int articleBufferSize, bool usePipelinedBodyRequests)
    {
        var path = $"/content/inconclusive-alternate-{Guid.NewGuid():N}.mkv";
        var cache = new ArticleMissNegativeCache(new ConfigManager());
        // The skipped provider already reported the primary missing; only the alternate is unproven.
        cache.MarkMissing(ArticleMissNegativeCache.BuildKey(
            MissingId, "a.example", null, ArticleMissNegativeCache.ArticleMissOperation.Body));
        using var client = CreateClient(firstProviderOpen: true, cache);
        await using var stream = CreateStream(
            client, [MissingId, OkId], articleBufferSize, usePipelinedBodyRequests, path,
            segmentFallbacks: [[AlternateId], []]);

        var miss = await Assert.ThrowsAsync<UsenetArticleNotFoundException>(
            async () => await stream.CopyToAsync(Stream.Null));

        Assert.Equal(MissingId, miss.SegmentId);
        Assert.Equal(MultiProviderNntpClient.InconclusiveMissReason, miss.InconclusiveReason);
    }

    private static MultiProviderNntpClient CreateClient(
        bool firstProviderOpen,
        ArticleMissNegativeCache? articleMissCache = null)
    {
        var segments = new Dictionary<string, byte[]> { [OkId] = "abcd"u8.ToArray() };
        return new MultiProviderNntpClient(
        [
            MultiProviderNntpClientTests.CreateProvider(
                new FakeNntpClient(segments, useCachedYencStreams: true),
                host: "a.example",
                circuitBreaker: firstProviderOpen
                    ? MultiProviderNntpClientTests.OpenBreaker("a.example")
                    : null,
                maxConnections: 2),
            MultiProviderNntpClientTests.CreateProvider(
                new FakeNntpClient(segments, useCachedYencStreams: true),
                host: "b.example",
                maxConnections: 2),
        ], articleMissCache: articleMissCache);
    }

    private static Stream CreateStream(
        MultiProviderNntpClient client,
        string[] segmentIds,
        int articleBufferSize,
        bool usePipelinedBodyRequests,
        string path,
        string[][]? segmentFallbacks = null,
        bool failFastOnFirstSegment = true) =>
        MultiSegmentStream.Create(
            segmentIds.AsMemory(),
            client,
            articleBufferSize,
            estimatedSegmentSize: 4,
            failFastOnFirstSegment,
            usePipelinedBodyRequests,
            CancellationToken.None,
            fileName: path,
            segmentFallbacks: segmentFallbacks,
            exactSegmentSizes: Enumerable.Repeat(4L, segmentIds.Length).ToArray());

    private static async Task WithPar2SinkAsync(Func<Par2RepairService, Task> body)
    {
        var dir = Path.Join(Path.GetTempPath(), "nzbdav-inconclusive-sink-" + Guid.NewGuid().ToString("N"));
        var previous = Par2RepairTriggerSink.Current;
        try
        {
            Directory.CreateDirectory(dir);
            var config = new ConfigManager();
            config.UpdateValues(
            [
                new ConfigItem { ConfigName = ConfigKeys.RepairEnable, ConfigValue = "true" },
            ]);
            var store = new RepairPatchStore(dir, 1024 * 1024);
            await store.EnsureCatalogLoadedAsync(CancellationToken.None);
            var service = new Par2RepairService(config, null!, store);
            Par2RepairTriggerSink.Current = new Par2RepairTriggerSink(service);
            await body(service);
        }
        finally
        {
            Par2RepairTriggerSink.Current = previous;
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}