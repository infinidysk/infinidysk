using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Microsoft.AspNetCore.Http;
using Serilog;

namespace NzbWebDAV.Middlewares;

/// <summary>
/// Records slow and failed WebDAV requests so a transient scan-time failure is visible in
/// logs and in the support pack. Only paths under the WebDAV mount roots are counted.
///
/// Slowness is attributed by where the time went rather than by total request duration.
/// For GETs the decisive clock runs to the first response byte (path resolution, first
/// NNTP fetch, decode): a ranged stream's total duration is paced by the client and says
/// nothing about the server. Metadata methods (PROPFIND, HEAD, ...) keep total-duration
/// semantics because their responses are small. A GET with a fast first byte whose total
/// duration exceeds the stall threshold is counted separately as a stalled stream. Slow
/// warnings are throttled per category so a busy host cannot flood the warnings ring
/// that support packs rely on.
///
/// Known limitation: first-byte timing wraps Response.Body and does not observe
/// IHttpResponseBodyFeature sendfile paths; the WebDAV GET path streams via Response.Body.
/// </summary>
public class WebDavObservabilityMiddleware(RequestDelegate next)
{
    private static readonly TimeSpan SlowThreshold = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan WarningInterval = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<string, long> Counters = new(StringComparer.Ordinal);

    // Test seams: the production defaults are impractical to exercise in a unit test.
    internal static TimeSpan? SlowThresholdOverride { get; set; }
    internal static TimeSpan? StallThresholdOverride { get; set; }
    internal static TimeSpan? WarningIntervalOverride { get; set; }

    private static readonly WarningThrottle FirstByteThrottle = new();
    private static readonly WarningThrottle MetadataThrottle = new();
    private static readonly WarningThrottle StallThrottle = new();

    private static readonly HashSet<string> WebDavRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "/content",
        "/completed-symlinks",
        "/.ids",
        "/nzbs",
        "/view",
    };

    // Public so test theories can take it as a parameter; ClassifySlow itself stays internal.
    public enum SlowKind
    {
        None,
        FirstByte,
        Metadata,
        LongStream,
        Stalled,
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsWebDavRequest(context))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var method = context.Request.Method;
        var isGet = HttpMethods.IsGet(method);
        var stopwatch = Stopwatch.StartNew();

        var originalBody = context.Response.Body;
#pragma warning disable CA2000 // wrapper ownership transfers to the response for the duration of next(); it owns no resources and never disposes the wrapped body
        var recordingStream = isGet ? new FirstByteRecordingStream(originalBody, stopwatch) : null;
#pragma warning restore CA2000
        if (recordingStream is not null)
            context.Response.Body = recordingStream;

        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            stopwatch.Stop();
            if (recordingStream is not null)
                context.Response.Body = originalBody;

            var status = context.Response.StatusCode;
            var elapsedMs = stopwatch.ElapsedMilliseconds;
            var path = context.Request.Path.Value ?? context.Request.Path.ToUriComponent();
            var firstByteMs = recordingStream?.FirstByteElapsedMilliseconds;

            Increment("total");

            var failed = status >= 500;
            if (failed) Increment("failed");

            if (context.RequestAborted.IsCancellationRequested)
            {
                Increment("aborted");
                // recordingStream exists exactly for GETs; a null first-byte mark
                // means the client left before any body byte went out.
                if (recordingStream is not null && firstByteMs is null)
                    Increment("abortedBeforeFirstByte");
            }

            if (failed)
            {
                Log.Warning(
                    "WebDAV request failed. Method={Method} Path={Path} Status={Status} DurationMs={DurationMs}",
                    method, path, status, elapsedMs);
            }

            switch (ClassifySlow(isGet, firstByteMs, elapsedMs))
            {
                case SlowKind.FirstByte:
                    Increment("slowFirstByte");
                    Increment("slow");
                    if (!failed && FirstByteThrottle.TryEmit(out var firstByteSuppressed))
                        Log.Warning(
                            "Slow WebDAV first byte. Method={Method} Path={Path} Status={Status} " +
                            "FirstByteMs={FirstByteMs} DurationMs={DurationMs} Suppressed={Suppressed}",
                            method, path, status, firstByteMs ?? elapsedMs, elapsedMs, firstByteSuppressed);
                    break;

                case SlowKind.Metadata:
                    Increment("slowMetadata");
                    Increment("slow");
                    if (!failed && MetadataThrottle.TryEmit(out var metadataSuppressed))
                        Log.Warning(
                            "Slow WebDAV metadata request. Method={Method} Path={Path} Status={Status} " +
                            "DurationMs={DurationMs} Suppressed={Suppressed}",
                            method, path, status, elapsedMs, metadataSuppressed);
                    break;

                case SlowKind.Stalled:
                    Increment("stalledStreams");
                    Increment("slow");
                    if (!failed && StallThrottle.TryEmit(out var stallSuppressed))
                        Log.Warning(
                            "Stalled WebDAV stream. Method={Method} Path={Path} Status={Status} " +
                            "FirstByteMs={FirstByteMs} DurationMs={DurationMs} Suppressed={Suppressed}",
                            method, path, status, firstByteMs ?? 0, elapsedMs, stallSuppressed);
                    break;

                case SlowKind.LongStream:
                    Increment("longStreams");
                    break;
            }
        }
    }

    // Pure classification so the timing tiers are testable without clock-dependent tests.
    internal static SlowKind ClassifySlow(bool isGet, long? firstByteMs, long elapsedMs)
    {
        var slowMs = (SlowThresholdOverride ?? SlowThreshold).TotalMilliseconds;
        var stallMs = (StallThresholdOverride ?? StallThreshold).TotalMilliseconds;

        if (!isGet)
            return elapsedMs >= slowMs ? SlowKind.Metadata : SlowKind.None;

        if (firstByteMs is not long firstByte)
            return elapsedMs >= slowMs ? SlowKind.FirstByte : SlowKind.None;

        if (firstByte >= slowMs)
            return SlowKind.FirstByte;

        if (elapsedMs >= stallMs)
            return SlowKind.Stalled;

        return elapsedMs >= slowMs ? SlowKind.LongStream : SlowKind.None;
    }

    private static bool IsWebDavRequest(HttpContext context)
    {
        var path = context.Request.Path.Value;
        if (string.IsNullOrEmpty(path))
            return false;

        return WebDavRoots.Any(root =>
            path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && (path.Length == root.Length || path[root.Length] == '/'));
    }

    private static void Increment(string key) =>
        Counters.AddOrUpdate(key, 1, (_, count) => count + 1);

    internal static IReadOnlyDictionary<string, long> Snapshot() =>
        new Dictionary<string, long>(Counters, StringComparer.Ordinal);

    internal static void Reset()
    {
        Counters.Clear();
        FirstByteThrottle.Reset();
        MetadataThrottle.Reset();
        StallThrottle.Reset();
    }

    /// <summary>
    /// Bounds slow-warning volume per category so a busy host cannot flood the warnings
    /// ring. Global rather than per path: a flood is spread across many distinct paths,
    /// so per-path throttling would not contain it.
    /// </summary>
    private sealed class WarningThrottle
    {
        private long _lastEmitTimestamp;
        private long _suppressed;

        public bool TryEmit(out long suppressed)
        {
            var now = Stopwatch.GetTimestamp();
            var intervalTicks =
                (long)((WarningIntervalOverride ?? WarningInterval).TotalSeconds * Stopwatch.Frequency);

            while (true)
            {
                var last = Interlocked.Read(ref _lastEmitTimestamp);
                if (last != 0 && now - last < intervalTicks)
                {
                    Interlocked.Increment(ref _suppressed);
                    Increment("suppressedSlowWarnings");
                    suppressed = 0;
                    return false;
                }

                if (Interlocked.CompareExchange(ref _lastEmitTimestamp, now, last) == last)
                {
                    suppressed = Interlocked.Exchange(ref _suppressed, 0);
                    return true;
                }
            }
        }

        public void Reset()
        {
            Interlocked.Exchange(ref _lastEmitTimestamp, 0);
            Interlocked.Exchange(ref _suppressed, 0);
        }
    }

    /// <summary>
    /// Records the elapsed time of the first body write; pass-through otherwise.
    /// </summary>
    private sealed class FirstByteRecordingStream(Stream inner, Stopwatch stopwatch) : Stream
    {
        private long _firstByteMs;
        private int _firstByteRecorded;

        public long? FirstByteElapsedMilliseconds
        {
            get
            {
                if (Volatile.Read(ref _firstByteRecorded) == 0)
                    return null;
                return Interlocked.Read(ref _firstByteMs);
            }
        }

        private void RecordFirstByte(int count)
        {
            // Zero-length writes carry no body byte; recording them would mark a
            // first byte that never went out and misclassify the request.
            if (count <= 0) return;
            if (Volatile.Read(ref _firstByteRecorded) != 0) return;
            // Value before the flag so a reader that observes the flag also
            // observes the timestamp (release/acquire pairing).
            Interlocked.Exchange(ref _firstByteMs, stopwatch.ElapsedMilliseconds);
            Volatile.Write(ref _firstByteRecorded, 1);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            RecordFirstByte(count);
            inner.Write(buffer, offset, count);
        }

        public override async Task WriteAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            RecordFirstByte(count);
            await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            RecordFirstByte(buffer.Length);
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
    }
}
