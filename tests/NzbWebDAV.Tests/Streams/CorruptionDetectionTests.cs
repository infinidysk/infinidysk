using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Streams;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Tests.Streams;

public class CorruptionDetectionTests
{
    [Fact]
    public async Task CorruptionDetectingStream_AddsSegmentAndProviderContext()
    {
        await using var stream = new CorruptionDetectingYencStream(
            new ThrowingYencStream(new InvalidDataException("CRC mismatch")),
            "segment@example",
            "provider-a");

        var exception = await Assert.ThrowsAsync<UsenetCorruptArticleException>(async () =>
            await stream.ReadExactlyAsync(new byte[1]));

        Assert.Equal("segment@example", exception.SegmentId);
        Assert.Equal("provider-a", exception.ProviderKey);
        Assert.IsType<InvalidDataException>(exception.InnerException);
    }

    [Fact]
    public async Task BufferedStream_RetriesCorruptionWithoutZeroFilling()
    {
        var expected = "validated payload"u8.ToArray();
        using var client = new CorruptThenValidNntpClient(expected, corruptResponses: 3);
        await using var stream = MultiSegmentStream.Create(
            new[] { "segment@example" }.AsMemory(),
            client,
            articleBufferSize: 1,
            estimatedSegmentSize: expected.Length,
            failFastOnFirstSegment: false,
            usePipelinedBodyRequests: false,
            CancellationToken.None);
        using var output = new MemoryStream();

        await stream.CopyToAsync(output);

        Assert.Equal(expected, output.ToArray());
        Assert.Equal(4, client.BodyRequestCount);
    }

    [Fact]
    public async Task UnbufferedStream_PreEmissionCorruption_RetriesAndServesValidBytes()
    {
        var expected = "validated payload"u8.ToArray();
        using var client = new ScriptedNntpClient((id, n) => n == 1
            ? ImmediateCorruptStream(id)
            : new BytesYencStream(expected));
        await using var stream = CreateUnbuffered(client, ["segment@example"], exactSizes: [expected.Length]);
        using var output = new MemoryStream();

        await stream.CopyToAsync(output);

        Assert.Equal(expected, output.ToArray());
        Assert.Equal(2, client.BodyRequestCount);
        Assert.Equal(0, client.OverlappingFetches);
    }

    [Fact]
    public async Task UnbufferedStream_PersistentPreEmissionCorruption_ZeroFillsAndReportsCorruption()
    {
        var fill = 8;
        var segmentId = $"segment-{Guid.NewGuid():N}@example";
        var fileName = $"movie-{Guid.NewGuid():N}.mkv";
        var reports = Par2RepairTriggerSink.TestReports ??= new();
        using var client = new ScriptedNntpClient((id, _) => ImmediateCorruptStream(id));
        await using var stream = CreateUnbuffered(
            client, [segmentId], exactSizes: [fill], fileName: fileName);
        using var output = new MemoryStream();

        await stream.CopyToAsync(output);

        Assert.Equal(new byte[fill], output.ToArray());
        Assert.Equal(4, client.BodyRequestCount);
        var report = Assert.Single(reports, e => e.SegmentId == segmentId);
        Assert.Equal(fileName, report.Path);
        Assert.True(report.IsCorruption);
    }

    [Fact]
    public async Task UnbufferedStream_ExactSizeCorruptTrailer_SurfacesCorruptionInsteadOfSilentGarbage()
    {
        var payload = "hello"u8.ToArray();
        var segmentId = $"segment-{Guid.NewGuid():N}@example";
        var reports = Par2RepairTriggerSink.TestReports ??= new();
        using var client = new ScriptedNntpClient(
            (id, _) => new PayloadThenCorruptYencStream(payload, id));
        await using var stream = CreateUnbuffered(
            client, [segmentId], exactSizes: [payload.Length]);
        using var output = new MemoryStream();

        var exception = await Assert.ThrowsAsync<UsenetCorruptArticleException>(
            () => stream.CopyToAsync(output));

        Assert.Equal(segmentId, exception.SegmentId);
        Assert.Equal(payload, output.ToArray());
        Assert.Equal(2, client.BodyRequestCount);
        var report = Assert.Single(reports, e => e.SegmentId == segmentId);
        Assert.True(report.IsCorruption);
    }

    [Fact]
    public async Task UnbufferedStream_PostEmissionCleanConfirmation_ThrowsTransientWithoutReporting()
    {
        var payload = "hello"u8.ToArray();
        var segmentId = $"segment-{Guid.NewGuid():N}@example";
        var reports = Par2RepairTriggerSink.TestReports ??= new();
        using var client = new ScriptedNntpClient((id, n) => n == 1
            ? new PayloadThenCorruptYencStream(payload, id)
            : new BytesYencStream(payload));
        await using var stream = CreateUnbuffered(
            client, [segmentId], exactSizes: [payload.Length]);
        using var output = new MemoryStream();

        var exception = await Assert.ThrowsAsync<TransientSegmentExhaustionException>(
            () => stream.CopyToAsync(output));

        Assert.Contains("transient/provider-specific", exception.Message, StringComparison.Ordinal);
        Assert.Contains(segmentId, exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
        Assert.False(exception.TryGetCausingException(out UsenetCorruptArticleException? _));
        Assert.Equal(2, client.BodyRequestCount);
        Assert.DoesNotContain(reports, e => e.SegmentId == segmentId);
    }

    [Fact]
    public async Task UnbufferedStream_PostEmissionCorruptConfirmation_RethrowsAndReports()
    {
        var payload = "hello"u8.ToArray();
        var segmentId = $"segment-{Guid.NewGuid():N}@example";
        var reports = Par2RepairTriggerSink.TestReports ??= new();
        using var client = new ScriptedNntpClient(
            (id, _) => new PayloadThenCorruptYencStream(payload, id));
        await using var stream = CreateUnbuffered(
            client, [segmentId], exactSizes: [payload.Length]);
        using var output = new MemoryStream();

        var exception = await Assert.ThrowsAsync<UsenetCorruptArticleException>(
            () => stream.CopyToAsync(output));

        Assert.Equal(segmentId, exception.SegmentId);
        Assert.True(Assert.Single(reports, e => e.SegmentId == segmentId).IsCorruption);
        Assert.Equal(2, client.BodyRequestCount);
    }

    [Fact]
    public async Task UnbufferedStream_FailFastOnFirstSegment_RethrowsPersistentCorruption()
    {
        using var client = new ScriptedNntpClient((id, _) => ImmediateCorruptStream(id));
        await using var stream = CreateUnbuffered(
            client, ["segment@example"], exactSizes: [8], failFast: true);
        using var output = new MemoryStream();

        var exception = await Assert.ThrowsAsync<UsenetCorruptArticleException>(
            () => stream.CopyToAsync(output));

        Assert.Equal("segment@example", exception.SegmentId);
        Assert.Equal(4, client.BodyRequestCount);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task UnbufferedStream_CorruptDonor_IsSkippedForNextDonor()
    {
        var expected = "donor-ok"u8.ToArray();
        using var client = new ScriptedNntpClient((id, _) => id switch
        {
            "primary@example" or "donor-corrupt@example" => ImmediateCorruptStream(id),
            "donor-good@example" => new BytesYencStream(expected),
            _ => throw new InvalidOperationException(id),
        });
        await using var stream = CreateUnbuffered(
            client,
            ["primary@example"],
            exactSizes: [expected.Length],
            fallbacks: [["donor-corrupt@example", "donor-good@example"]]);
        using var output = new MemoryStream();

        await stream.CopyToAsync(output);

        Assert.Equal(expected, output.ToArray());
        Assert.Equal(4, client.BodyRequestCounts["primary@example"]);
        Assert.Equal(1, client.BodyRequestCounts["donor-corrupt@example"]);
        Assert.Equal(1, client.BodyRequestCounts["donor-good@example"]);
    }

    [Fact]
    public async Task UnbufferedStream_DisposesFailedBodyBeforeRefetch()
    {
        var expected = "validated payload"u8.ToArray();
        using var client = new ScriptedNntpClient((id, n) => n <= 2
            ? ImmediateCorruptStream(id)
            : new BytesYencStream(expected));
        await using var stream = CreateUnbuffered(client, ["segment@example"], exactSizes: [expected.Length]);
        using var output = new MemoryStream();

        await stream.CopyToAsync(output);

        Assert.Equal(expected, output.ToArray());
        Assert.Equal(3, client.BodyRequestCount);
        Assert.Equal(0, client.OverlappingFetches);
    }

    [Fact]
    public async Task UnbufferedStream_UnknownLengthPersistentCorruption_PreservesCorruptInner()
    {
        using var client = new ScriptedNntpClient((id, _) => ImmediateCorruptStream(id));
        await using var stream = CreateUnbuffered(client, ["segment@example"]);
        using var output = new MemoryStream();

        var exception = await Assert.ThrowsAsync<RetryableDownloadException>(
            () => stream.CopyToAsync(output));

        Assert.False(exception is UsenetCorruptArticleException);
        Assert.IsType<UsenetCorruptArticleException>(exception.InnerException);
        Assert.Contains("exact length is unknown", exception.Message, StringComparison.Ordinal);
    }

    private static Stream CreateUnbuffered(
        INntpClient client,
        string[] segmentIds,
        long[]? exactSizes = null,
        string[][]? fallbacks = null,
        bool failFast = false,
        string? fileName = "movie.mkv") =>
        MultiSegmentStream.Create(
            segmentIds.AsMemory(),
            client,
            articleBufferSize: 0,
            estimatedSegmentSize: exactSizes is { Length: > 0 } ? exactSizes[0] : 16,
            failFastOnFirstSegment: failFast,
            usePipelinedBodyRequests: false,
            CancellationToken.None,
            fileName: fileName,
            segmentFallbacks: fallbacks,
            exactSegmentSizes: exactSizes);

    private static YencStream ImmediateCorruptStream(string segmentId) =>
        new ThrowingYencStream(
            new UsenetCorruptArticleException(segmentId, "provider-a",
                new InvalidDataException("CRC mismatch")));

    private sealed class PayloadThenCorruptYencStream(byte[] bytes, string segmentId) : YencStream(Null)
    {
        private int _position;

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_position < bytes.Length)
            {
                var count = Math.Min(buffer.Length, bytes.Length - _position);
                bytes.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return ValueTask.FromResult(count);
            }

            return ValueTask.FromException<int>(
                new UsenetCorruptArticleException(segmentId, "provider-a",
                    new InvalidDataException("CRC mismatch")));
        }
    }

    private sealed class ScriptedNntpClient(Func<string, int, YencStream> factory) : NntpClient
    {
        public int BodyRequestCount { get; private set; }
        public Dictionary<string, int> BodyRequestCounts { get; } = new(StringComparer.Ordinal);
        public int OverlappingFetches { get; private set; }
        private int _openStreams;

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            CancellationToken cancellationToken) =>
            CreateResponse(segmentId);

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            CreateResponse(segmentId);

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            UsenetExclusiveConnection exclusiveConnection,
            CancellationToken cancellationToken) =>
            CreateResponse(segmentId);

        private Task<UsenetDecodedBodyResponse> CreateResponse(SegmentId segmentId)
        {
            BodyRequestCount++;
            var key = segmentId.ToString();
            BodyRequestCounts[key] = BodyRequestCounts.GetValueOrDefault(key) + 1;
            if (_openStreams > 0)
                OverlappingFetches++;

            YencStream inner;
            try
            {
                inner = factory(key, BodyRequestCounts[key]);
            }
            catch (Exception)
            {
                return Task.FromException<UsenetDecodedBodyResponse>(
                    new UsenetCorruptArticleException(key, "provider-a",
                        new InvalidDataException("CRC mismatch")));
            }

            _openStreams++;
            var stream = new TrackingYencStream(inner, () => _openStreams--);
            return Task.FromResult(new UsenetDecodedBodyResponse
            {
                SegmentId = segmentId,
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                ResponseMessage = "body follows",
                Stream = stream,
            });
        }

        public override Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
            string segmentId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new UsenetExclusiveConnection(null));

        public override Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
            IReadOnlyList<SegmentId> segmentIds,
            CancellationToken cancellationToken) =>
            Task.FromResult(new UsenetExclusiveConnection(null));

        public override Task ConnectAsync(
            string host, int port, bool useSsl, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task<UsenetResponse> AuthenticateAsync(
            string user, string pass, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetHeadResponse> HeadAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override void Dispose()
        {
        }
    }

    private sealed class TrackingYencStream(YencStream inner, Action onDispose) : YencStream(Null)
    {
        private int _disposed;

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override ValueTask<UsenetYencHeader?> GetYencHeadersAsync(
            CancellationToken cancellationToken = default) =>
            inner.GetYencHeadersAsync(cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                onDispose();
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                onDispose();
            if (inner is IAsyncDisposable asyncInner)
                await asyncInner.DisposeAsync().ConfigureAwait(false);
            else
                inner.Dispose();
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class ThrowingYencStream(Exception exception) : YencStream(Null)
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(exception);
    }

    private sealed class BytesYencStream(byte[] bytes) : YencStream(Null)
    {
        private int _position;

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_position >= bytes.Length)
                return ValueTask.FromResult(0);
            var count = Math.Min(buffer.Length, bytes.Length - _position);
            bytes.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return ValueTask.FromResult(count);
        }
    }

    private sealed class CorruptThenValidNntpClient(
        byte[] validPayload,
        int corruptResponses) : NntpClient
    {
        public int BodyRequestCount { get; private set; }

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            CancellationToken cancellationToken) =>
            CreateResponse(segmentId);

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            CreateResponse(segmentId);

        public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
            SegmentId segmentId,
            UsenetExclusiveConnection exclusiveConnection,
            CancellationToken cancellationToken) =>
            CreateResponse(segmentId);

        private Task<UsenetDecodedBodyResponse> CreateResponse(SegmentId segmentId)
        {
            BodyRequestCount++;
            YencStream stream = BodyRequestCount <= corruptResponses
                ? new ThrowingYencStream(
                    new UsenetCorruptArticleException(segmentId, "provider-a",
                        new InvalidDataException("CRC mismatch")))
                : new BytesYencStream(validPayload);
            return Task.FromResult(new UsenetDecodedBodyResponse
            {
                SegmentId = segmentId,
                ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
                ResponseMessage = "body follows",
                Stream = stream,
            });
        }

        public override Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
            string segmentId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new UsenetExclusiveConnection(null));

        public override Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
            IReadOnlyList<SegmentId> segmentIds,
            CancellationToken cancellationToken) =>
            Task.FromResult(new UsenetExclusiveConnection(null));

        public override Task ConnectAsync(
            string host, int port, bool useSsl, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override Task<UsenetResponse> AuthenticateAsync(
            string user, string pass, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetStatResponse> StatAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetHeadResponse> HeadAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
            IReadOnlyList<SegmentId> segmentIds,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
            SegmentId segmentId,
            ArticleBodyCompletionHandler? onConnectionReadyAgain,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override void Dispose()
        {
        }
    }
}
