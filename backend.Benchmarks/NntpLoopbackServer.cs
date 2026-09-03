using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace NzbWebDAV.Benchmarks;

/// <summary>
/// Small deterministic NNTP server used only by the whole-path benchmark. It accepts
/// pipelined BODY commands and writes precomputed wire bodies in command order.
/// </summary>
internal sealed class NntpLoopbackServer : IAsyncDisposable
{
    private const int MaximumCommandLength = 4096;
    private readonly TcpListener _listener;
    private readonly NntpLoopbackCorpus _corpus;
    private readonly LoopbackNetworkImpairment _impairment;
    private readonly HashSet<string> _missingIds;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<Task> _connectionTasks = [];
    private readonly Lock _connectionTimingLock = new();
    private readonly Task _acceptTask;
    private int _activeConnections;
    private int _peakActiveConnections;
    private long _firstAcceptedTimestamp;
    private long _lastPeakTimestamp;
    private long _bodyCommands;
    private long _responses;
    private long _wireBytes;

    private NntpLoopbackServer(
        NntpLoopbackCorpus corpus,
        LoopbackNetworkImpairment impairment,
        IEnumerable<string>? missingIds)
    {
        _corpus = corpus;
        _impairment = impairment;
        _missingIds = missingIds is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(missingIds.Select(NntpLoopbackCorpus.NormalizeSegmentId), StringComparer.Ordinal);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _acceptTask = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static Task<NntpLoopbackServer> StartAsync(
        NntpLoopbackCorpus corpus,
        int roundTripDelayMs = 0,
        long? bandwidthBytesPerSecond = null,
        int handshakeDelayMs = 0,
        IEnumerable<string>? missingIds = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(roundTripDelayMs);
        ArgumentOutOfRangeException.ThrowIfNegative(handshakeDelayMs);
        return Task.FromResult(new NntpLoopbackServer(
            corpus,
            new LoopbackNetworkImpairment(roundTripDelayMs, bandwidthBytesPerSecond, handshakeDelayMs),
            missingIds));
    }

    public NntpLoopbackServerSnapshot GetSnapshot()
    {
        long firstAccepted;
        long lastPeak;
        int peakActiveConnections;
        lock (_connectionTimingLock)
        {
            firstAccepted = _firstAcceptedTimestamp;
            lastPeak = _lastPeakTimestamp;
            peakActiveConnections = _peakActiveConnections;
        }
        var timeToPeakActiveMs = firstAccepted == 0 || lastPeak < firstAccepted
            ? 0
            : Stopwatch.GetElapsedTime(firstAccepted, lastPeak).TotalMilliseconds;
        return new NntpLoopbackServerSnapshot(
            Volatile.Read(ref _bodyCommands),
            Volatile.Read(ref _responses),
            Volatile.Read(ref _activeConnections),
            peakActiveConnections,
            Interlocked.Read(ref _wireBytes),
            timeToPeakActiveMs);
    }

    public async Task RunUntilCancelledAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
    }

    public async Task WriteSnapshotAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(GetSnapshot()),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task WaitForIdleAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        while (Volatile.Read(ref _activeConnections) != 0)
        {
            if (started.Elapsed >= timeout)
                throw new TimeoutException("Loopback NNTP connections did not close before the snapshot timeout.");
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                var task = ServeConnectionAsync(client);
                _connectionTasks.Add(task);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            return;
        }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested)
        {
            return;
        }
    }

    private async Task ServeConnectionAsync(TcpClient client)
    {
        var acceptedTimestamp = Stopwatch.GetTimestamp();
        var active = Interlocked.Increment(ref _activeConnections);
        lock (_connectionTimingLock)
        {
            if (_firstAcceptedTimestamp == 0)
                _firstAcceptedTimestamp = acceptedTimestamp;
            if (active > _peakActiveConnections)
            {
                _peakActiveConnections = active;
                _lastPeakTimestamp = acceptedTimestamp;
            }
        }
        try
        {
            using (client)
            await using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.Latin1, leaveOpen: true))
            {
                await _impairment.BeforeHandshakeStepAsync(_stop.Token).ConfigureAwait(false);
                await WriteAsciiAsync(stream, "200 loopback benchmark ready\r\n", _stop.Token).ConfigureAwait(false);
                while (!_stop.IsCancellationRequested)
                {
                    var command = await ReadCommandAsync(reader, _stop.Token).ConfigureAwait(false);
                    if (command is null)
                        return;
                    if (command.Equals("QUIT", StringComparison.Ordinal))
                    {
                        await WriteAsciiAsync(stream, "205 closing connection\r\n", _stop.Token).ConfigureAwait(false);
                        return;
                    }
                    if (command.StartsWith("AUTHINFO ", StringComparison.OrdinalIgnoreCase))
                    {
                        await _impairment.BeforeHandshakeStepAsync(_stop.Token).ConfigureAwait(false);
                        await WriteAsciiAsync(stream, "281 authentication accepted\r\n", _stop.Token).ConfigureAwait(false);
                        continue;
                    }
                    if (!TryParseBody(command, out var segmentId))
                    {
                        await WriteAsciiAsync(stream, "500 unsupported command\r\n", _stop.Token).ConfigureAwait(false);
                        continue;
                    }

                    Interlocked.Increment(ref _bodyCommands);
                    await _impairment.BeforeResponseAsync(_stop.Token).ConfigureAwait(false);
                    if (_missingIds.Contains(segmentId) || !_corpus.TryGetArticle(segmentId, out var article))
                    {
                        Interlocked.Increment(ref _responses);
                        await WriteAsciiAsync(stream, "430 no such article\r\n", _stop.Token).ConfigureAwait(false);
                        continue;
                    }

                    await WriteAsciiAsync(stream, $"222 0 <{article.SegmentId}> body follows\r\n", _stop.Token)
                        .ConfigureAwait(false);
                    await _impairment.WriteAsync(stream, article.WireBody, _stop.Token).ConfigureAwait(false);
                    Interlocked.Increment(ref _responses);
                    Interlocked.Add(ref _wireBytes, article.WireBody.Length);
                }
            }
        }
        catch (Exception ex) when (_stop.IsCancellationRequested &&
            ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Client close and shutdown cancellation both tear down the read loop.
        }
        finally
        {
            Interlocked.Decrement(ref _activeConnections);
        }
    }

    private static async Task<string?> ReadCommandAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var command = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (command is { Length: > MaximumCommandLength })
            throw new InvalidDataException("Loopback NNTP command exceeded the configured limit.");
        return command;
    }

    private static bool TryParseBody(string command, out string segmentId)
    {
        segmentId = string.Empty;
        if (!command.StartsWith("BODY <", StringComparison.OrdinalIgnoreCase) ||
            !command.EndsWith('>') ||
            command.Length <= "BODY <>".Length)
        {
            return false;
        }
        segmentId = NntpLoopbackCorpus.NormalizeSegmentId(command[5..^1]);
        return segmentId.Length > 0 && segmentId.Length <= MaximumCommandLength - 7;
    }

    private static Task WriteAsciiAsync(Stream stream, string value, CancellationToken cancellationToken) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes(value), cancellationToken).AsTask();

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        _listener.Dispose();
        try
        {
            await _acceptTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // Cancellation is the expected listener shutdown path.
        }

        await Task.WhenAll(_connectionTasks.Select(ObserveShutdownAsync)).ConfigureAwait(false);
        _stop.Dispose();
    }

    private static async Task ObserveShutdownAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Connection tasks observe the shutdown token or a closed peer.
        }
    }
}

internal sealed class LoopbackNetworkImpairment(
    int roundTripDelayMs,
    long? bandwidthBytesPerSecond,
    int handshakeDelayMs)
{
    private const int WriteChunkBytes = 64 * 1024;

    public Task BeforeHandshakeStepAsync(CancellationToken cancellationToken) =>
        handshakeDelayMs == 0
            ? Task.CompletedTask
            : Task.Delay(TimeSpan.FromMilliseconds(handshakeDelayMs), cancellationToken);

    public Task BeforeResponseAsync(CancellationToken cancellationToken) =>
        roundTripDelayMs == 0
            ? Task.CompletedTask
            : Task.Delay(TimeSpan.FromMilliseconds(roundTripDelayMs), cancellationToken);

    public async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        for (var offset = 0; offset < body.Length; offset += WriteChunkBytes)
        {
            var chunk = body.Slice(offset, Math.Min(WriteChunkBytes, body.Length - offset));
            await stream.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (bandwidthBytesPerSecond is > 0)
            {
                var delay = TimeSpan.FromSeconds((double)chunk.Length / bandwidthBytesPerSecond.Value);
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

internal sealed record NntpLoopbackServerSnapshot(
    long BodyCommands,
    long Responses,
    int ActiveConnections,
    int PeakActiveConnections,
    long WireBytes,
    double TimeToPeakActiveMs);
