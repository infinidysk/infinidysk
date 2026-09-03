using System.Text;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models;
using NzbWebDAV.Streams;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Tests.Fakes;

internal sealed class FakeNntpClient(
    IReadOnlyDictionary<string, byte[]> segments,
    bool useCachedYencStreams = false,
    IReadOnlyDictionary<string, LongRange>? segmentRanges = null,
    Func<string, byte[], Stream>? decodedStreamFactory = null,
    IReadOnlyDictionary<string, byte[]>? localSegments = null) : NntpClient
{
    // Copied at construction so tests can add/restore articles via Serve() without
    // mutating the caller's dictionary.
    private readonly Dictionary<string, byte[]> _segments = new(segments, StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, byte[]> _localSegments =
        localSegments ?? new Dictionary<string, byte[]>(StringComparer.Ordinal);

    public int BatchRequestCount { get; private set; }
    public int BodyRequestCount { get; private set; }
    public int HeaderProbeCount { get; private set; }
    public int CompletionCallbackCount { get; private set; }
    public int? LastPrewarmTarget { get; private set; }
    public CancellationToken LastBatchToken { get; private set; }
    public TaskCompletionSource FirstBatchRequested { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Dictionary<string, int> BodyRequestCounts { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> CompletionCallbackCounts { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> StatRequestCounts { get; } = new(StringComparer.Ordinal);
    public List<string> StatRequestOrder { get; } = [];
    public HashSet<string> RequestedSegmentIds { get; } = new(StringComparer.Ordinal);

    /// <summary>Adds or restores an article (e.g. provider-side recovery between checks).</summary>
    public void Serve(string segmentId, byte[] content) => _segments[segmentId] = content;

    /// <summary>
    /// When set, BODY responses claim this SegmentId instead of the requested one
    /// so tests can exercise mismatch rejection without a live NNTP scramble.
    /// </summary>
    public string? ForcedResponseSegmentId { get; set; }

    public override Task PrewarmConnectionsAsync(
        int targetConnections,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastPrewarmTarget = targetConnections;
        return Task.CompletedTask;
    }

    public override Task ConnectAsync(
        string host, int port, bool useSsl, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public override Task<UsenetResponse> AuthenticateAsync(
        string user, string pass, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public override Task<UsenetStatResponse> StatAsync(
        SegmentId segmentId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = segmentId.ToString();
        StatRequestCounts[key] = StatRequestCounts.GetValueOrDefault(key) + 1;
        StatRequestOrder.Add(key);
        var exists = _segments.ContainsKey(key);
        return Task.FromResult(new UsenetStatResponse
        {
            ResponseCode = exists
                ? (int)UsenetResponseType.ArticleExists
                : (int)UsenetResponseType.NoArticleWithThatMessageId,
            ResponseMessage = exists
                ? $"223 0 0 <{key}>"
                : $"430 No such article <{key}>",
            ArticleExists = exists,
        });
    }

    public override Task<UsenetHeadResponse> HeadAsync(
        SegmentId segmentId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public override Task<UsenetYencHeader> GetYencHeadersAsync(string segmentId, CancellationToken ct)
    {
        HeaderProbeCount++;
        return base.GetYencHeadersAsync(segmentId, ct);
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId, CancellationToken cancellationToken) =>
        DecodedBodyAsync(segmentId, null, cancellationToken);

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId,
        ArticleBodyCompletionHandler? onConnectionReadyAgain,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BodyRequestCount++;
        var segmentKey = segmentId.ToString();
        RequestedSegmentIds.Add(segmentKey);
        BodyRequestCounts[segmentKey] = BodyRequestCounts.GetValueOrDefault(segmentKey) + 1;
        try
        {
            var response = CreateBodyResponse(segmentId);
            NoteCompletion(segmentKey);
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
            return Task.FromResult(response);
        }
        catch (Exception e)
        {
            // Return a faulted task so pipelined batch consumers can await
            // per-segment failures without aborting DecodedBodiesAsync itself.
            return Task.FromException<UsenetDecodedBodyResponse>(e);
        }
    }

    public override Task<UsenetDecodedBodyResponse?> TryGetLocalDecodedBodyAsync(
        SegmentId segmentId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = segmentId.ToString();
        return _localSegments.ContainsKey(key)
            ? Task.FromResult<UsenetDecodedBodyResponse?>(CreateBodyResponse(segmentId, _localSegments))
            : Task.FromResult<UsenetDecodedBodyResponse?>(null);
    }

    public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
        IReadOnlyList<SegmentId> segmentIds,
        ArticleBodyCompletionHandler? onConnectionReadyAgain,
        CancellationToken cancellationToken)
    {
        BatchRequestCount++;
        LastBatchToken = cancellationToken;
        FirstBatchRequested.TrySetResult();
        var responses = segmentIds
            .Select(segmentId => DecodedBodyAsync(segmentId, cancellationToken))
            .ToArray();
        onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
        return Task.FromResult(new UsenetDecodedBodyBatch { Responses = responses });
    }

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

    public override Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
        string segmentId, CancellationToken cancellationToken) =>
        Task.FromResult(new UsenetExclusiveConnection(null));

    public override Task<UsenetExclusiveConnection> AcquireExclusiveConnectionAsync(
        IReadOnlyList<SegmentId> segmentIds, CancellationToken cancellationToken) =>
        Task.FromResult(new UsenetExclusiveConnection(null));

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId,
        UsenetExclusiveConnection exclusiveConnection,
        CancellationToken cancellationToken) =>
        DecodedBodyAsync(segmentId, exclusiveConnection.OnConnectionReadyAgain, cancellationToken);

    public override Task<UsenetDecodedBodyBatch> DecodedBodiesAsync(
        IReadOnlyList<SegmentId> segmentIds,
        UsenetExclusiveConnection exclusiveConnection,
        CancellationToken cancellationToken) =>
        DecodedBodiesAsync(
            segmentIds, exclusiveConnection.OnConnectionReadyAgain, cancellationToken);

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId,
        UsenetExclusiveConnection exclusiveConnection,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public override void Dispose()
    {
    }

    private void NoteCompletion(string segmentKey)
    {
        CompletionCallbackCount++;
        CompletionCallbackCounts[segmentKey] = CompletionCallbackCounts.GetValueOrDefault(segmentKey) + 1;
    }

    private UsenetDecodedBodyResponse CreateBodyResponse(SegmentId segmentId) =>
        CreateBodyResponse(segmentId, _segments);

    private UsenetDecodedBodyResponse CreateBodyResponse(
        SegmentId segmentId,
        IReadOnlyDictionary<string, byte[]> segments)
    {
        var key = segmentId.ToString();
        if (!segments.TryGetValue(key, out var bytes))
            throw new UsenetArticleNotFoundException(key, "430 No such article");
        var range = default(LongRange);
        var hasRange = segmentRanges is not null && segmentRanges.TryGetValue(key, out range);

        YencStream stream = useCachedYencStreams
            ? new CachedYencStream(
                new UsenetYencHeader
                {
                    FileName = "fake.bin",
                    FileSize = segmentRanges is { Count: > 0 }
                        ? segmentRanges.Values.Max(range => range.EndExclusive)
                        : bytes.Length,
                    LineLength = 128,
                    PartNumber = 1,
                    TotalParts = segments.Count,
                    PartOffset = hasRange ? range!.StartInclusive : 0,
                    PartSize = hasRange ? range!.Count : bytes.Length,
                },
                decodedStreamFactory?.Invoke(key, bytes)
                    ?? new MemoryStream(bytes, writable: false))
            : new YencStream(new MemoryStream(EncodeYenc(bytes), writable: false));
        return new UsenetDecodedBodyResponse
        {
            SegmentId = ForcedResponseSegmentId ?? key,
            ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
            ResponseMessage = "222 fake body",
            Stream = stream,
        };
    }

    private static byte[] EncodeYenc(ReadOnlySpan<byte> source)
    {
        using var output = new MemoryStream(source.Length + 128);
        WriteAscii(output, $"=ybegin line=128 size={source.Length} name=fake.bin\r\n");
        var lineLength = 0;
        foreach (var value in source)
        {
            var encoded = unchecked((byte)(value + 42));
            if (encoded is 0 or (byte)'\n' or (byte)'\r' or (byte)'=')
            {
                output.WriteByte((byte)'=');
                output.WriteByte(unchecked((byte)(encoded + 64)));
                lineLength += 2;
            }
            else
            {
                output.WriteByte(encoded);
                lineLength++;
            }

            if (lineLength < 128) continue;
            WriteAscii(output, "\r\n");
            lineLength = 0;
        }

        if (lineLength > 0) WriteAscii(output, "\r\n");
        WriteAscii(output, $"=yend size={source.Length}\r\n");
        return output.ToArray();
    }

    private static void WriteAscii(Stream output, string value) =>
        output.Write(Encoding.ASCII.GetBytes(value));
}
