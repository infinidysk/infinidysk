using System.Collections.Concurrent;
using System.Text.Json;
using NzbWebDAV.Database.Models.Metrics;

namespace NzbWebDAV.Services.StreamTrace;

/// <summary>
/// In-memory ring buffer of playback stream events keyed by ReadSessionId.
/// Same lifetime model as LogBufferSink: process-local, dump before restart.
/// Enablement is runtime-togglable so Docker installs can capture traces without
/// setting STREAM_TRACE_EVENTS and restarting.
/// </summary>
public sealed class StreamTraceBuffer
{
    public const string SourceEnv = "env";
    public const string SourceUi = "ui";
    public const int UiMaxCapacity = 20_000;
    public const int EnvMaxCapacity = 200_000;
    public const int DefaultUiCapacity = 20_000;
    public static readonly int[] AllowedUiMinutes = [15, 30, 60];

    private static readonly JsonSerializerOptions CompactJson = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly int _maxSessions;
    private readonly object _gate = new();
    private volatile StreamTraceEvent?[] _buffer = [];
    private long _nextSequence;
    private long _expiresAtUnixMs;
    private string _source = SourceEnv;
    private int _capacity;

    // Newest session first for summary listing.
    private readonly ConcurrentDictionary<Guid, SessionMeta> _sessions = new();

    public StreamTraceBuffer(int capacity, int maxSessions = 200, bool enabled = true)
    {
        _maxSessions = Math.Max(10, maxSessions);
        if (enabled)
            EnableFor(TimeSpan.Zero, capacity, SourceEnv);
        else
            _capacity = Math.Max(100, capacity);
    }

    public int Capacity
    {
        get
        {
            lock (_gate) return Math.Max(_capacity, 100);
        }
    }

    /// <summary>
    /// Tracing is opt-in. When disabled, Record is a no-op so production
    /// deployments pay no memory or hot-path cost. Expiry is checked lock-free
    /// against a volatile snapshot so the disabled path stays allocation-free.
    /// </summary>
    public bool Enabled
    {
        get
        {
            var buffer = _buffer;
            if (buffer.Length == 0) return false;
            var expiresAt = Volatile.Read(ref _expiresAtUnixMs);
            return expiresAt == 0 || expiresAt > Now();
        }
    }

    /// <summary>
    /// Turns tracing on for <paramref name="ttl"/>. A zero TTL means no expiry
    /// (used by the STREAM_TRACE_EVENTS bootstrap path). UI callers must pass a
    /// positive TTL — the expiry sweeper will Disable() when it elapses.
    /// </summary>
    public StreamTraceStatus EnableFor(TimeSpan ttl, int capacity, string source)
    {
        var isUi = string.Equals(source, SourceUi, StringComparison.Ordinal);
        var capped = Math.Clamp(capacity, 100, isUi ? UiMaxCapacity : EnvMaxCapacity);
        var expiresAt = ttl <= TimeSpan.Zero
            ? 0L
            : Now() + (long)ttl.TotalMilliseconds;

        lock (_gate)
        {
            _buffer = new StreamTraceEvent?[capped];
            _capacity = capped;
            _source = isUi ? SourceUi : SourceEnv;
            Volatile.Write(ref _expiresAtUnixMs, expiresAt);
            _sessions.Clear();
            Interlocked.Exchange(ref _nextSequence, 0);
        }

        return GetStatus();
    }

    /// <summary>
    /// Releases the ring buffer and session index so the GC can reclaim the RAM.
    /// Safe to call when already disabled.
    /// </summary>
    public StreamTraceStatus Disable()
    {
        lock (_gate)
        {
            _buffer = [];
            Volatile.Write(ref _expiresAtUnixMs, 0);
            _sessions.Clear();
            Interlocked.Exchange(ref _nextSequence, 0);
        }

        return GetStatus();
    }

    /// <summary>
    /// True when a positive TTL has elapsed and Disable() has not yet run.
    /// </summary>
    public bool IsExpired
    {
        get
        {
            var expiresAt = Volatile.Read(ref _expiresAtUnixMs);
            return expiresAt > 0 && expiresAt <= Now() && _buffer.Length > 0;
        }
    }

    public StreamTraceStatus GetStatus()
    {
        lock (_gate)
        {
            var expiresAt = Volatile.Read(ref _expiresAtUnixMs);
            var enabled = _buffer.Length > 0 && (expiresAt == 0 || expiresAt > Now());
            return new StreamTraceStatus(
                Enabled: enabled,
                Source: _source,
                ExpiresAtUnixMs: expiresAt,
                Capacity: Math.Max(_capacity, 100),
                EventCount: Volatile.Read(ref _nextSequence),
                SessionCount: _sessions.Count);
        }
    }

    public void Record(StreamTraceEvent entry)
    {
        if (!Enabled) return;
        var sequence = Interlocked.Increment(ref _nextSequence);
        var withSeq = entry with { Sequence = sequence };
        var buffer = _buffer;
        if (buffer.Length == 0) return;
        lock (_gate)
        {
            buffer = _buffer;
            if (buffer.Length == 0) return;
            buffer[(sequence - 1) % buffer.Length] = withSeq;
        }

        _sessions.AddOrUpdate(
            entry.SessionId,
            _ => new SessionMeta
            {
                SessionId = entry.SessionId,
                FirstAt = entry.AtUnixMs,
                LastAt = entry.AtUnixMs,
                Path = entry.Path,
                EventCount = 1,
                LastKind = entry.Kind,
            },
            (_, existing) =>
            {
                existing.LastAt = entry.AtUnixMs;
                existing.EventCount++;
                existing.LastKind = entry.Kind;
                if (!string.IsNullOrEmpty(entry.Path)) existing.Path = entry.Path;
                return existing;
            });

        TrimSessionsIfNeeded();
    }

    public void RangeOpen(
        Guid sessionId,
        string path,
        string method,
        long rangeStart,
        long? rangeEnd,
        long? fileSize,
        string? userAgent,
        string? clientIp)
    {
        Record(new StreamTraceEvent
        {
            Sequence = 0,
            AtUnixMs = Now(),
            SessionId = sessionId,
            Kind = StreamTraceKind.RangeOpen.ToString(),
            Path = path,
            Method = method,
            RangeStart = rangeStart,
            RangeEnd = rangeEnd,
            FileSize = fileSize,
            UserAgent = userAgent,
            ClientIp = clientIp,
        });
    }

    public void Seek(Guid sessionId, long offset)
    {
        Record(new StreamTraceEvent
        {
            Sequence = 0,
            AtUnixMs = Now(),
            SessionId = sessionId,
            Kind = StreamTraceKind.Seek.ToString(),
            Offset = offset,
        });
    }

    public void Segment(
        Guid sessionId,
        string provider,
        SegmentFetch.FetchStatus status,
        int durationMs,
        int retries,
        string? segmentId = null)
    {
        Record(new StreamTraceEvent
        {
            Sequence = 0,
            AtUnixMs = Now(),
            SessionId = sessionId,
            Kind = StreamTraceKind.Segment.ToString(),
            Provider = provider,
            Status = StreamTraceEvent.StatusName(status),
            DurationMs = durationMs,
            Retries = retries,
            SegmentId = StreamTraceEvent.TruncateSegmentId(segmentId),
        });
    }

    public void ZeroFill(Guid sessionId, string segmentId, long bytes, string? message = null)
    {
        Record(new StreamTraceEvent
        {
            Sequence = 0,
            AtUnixMs = Now(),
            SessionId = sessionId,
            Kind = StreamTraceKind.ZeroFill.ToString(),
            SegmentId = StreamTraceEvent.TruncateSegmentId(segmentId),
            Bytes = bytes,
            Message = message,
        });
    }

    public void Failover(Guid sessionId, string fromProvider, string toProvider, string? reason = null)
    {
        Record(new StreamTraceEvent
        {
            Sequence = 0,
            AtUnixMs = Now(),
            SessionId = sessionId,
            Kind = StreamTraceKind.Failover.ToString(),
            FromProvider = fromProvider,
            ToProvider = toProvider,
            Status = reason,
        });
    }

    public void Retry(Guid sessionId, string segmentId, int attempt, string? message = null)
    {
        Record(new StreamTraceEvent
        {
            Sequence = 0,
            AtUnixMs = Now(),
            SessionId = sessionId,
            Kind = StreamTraceKind.Retry.ToString(),
            SegmentId = StreamTraceEvent.TruncateSegmentId(segmentId),
            Attempt = attempt,
            Message = message,
        });
    }

    /// <summary>
    /// Adds time spent blocked on <paramref name="kind"/> to the current range of
    /// <paramref name="sessionId"/>. Ticks are accumulated rather than milliseconds so
    /// the many sub-millisecond client writes in a range still add up. No-ops when
    /// tracing is off or the session has no open range.
    /// </summary>
    public void AddStall(Guid sessionId, StreamStallKind kind, TimeSpan elapsed)
    {
        if (!Enabled || elapsed <= TimeSpan.Zero) return;
        if (!_sessions.TryGetValue(sessionId, out var session)) return;
        session.Stalls.Add(kind, elapsed.Ticks);
    }

    /// <summary>
    /// Records a connection acquisition for the current range: how long the borrower
    /// waited, and whether the pool handed back an idle connection or had to open a
    /// new one. A range full of fresh handshakes points at connection churn.
    /// </summary>
    public void ConnectionAcquired(Guid sessionId, TimeSpan wait, bool wasReused)
    {
        if (!Enabled) return;
        if (!_sessions.TryGetValue(sessionId, out var session)) return;
        session.Stalls.AddConnection(wait.Ticks, wasReused);
    }

    public void RangeEnd(
        Guid sessionId,
        ReadSession.EndReasonCode endReason,
        long bytesServed,
        string? message = null)
    {
        var stalls = _sessions.TryGetValue(sessionId, out var session)
            ? session.Stalls.DrainForRange()
            : default;

        Record(new StreamTraceEvent
        {
            Sequence = 0,
            AtUnixMs = Now(),
            SessionId = sessionId,
            Kind = StreamTraceKind.RangeEnd.ToString(),
            EndReason = StreamTraceEvent.EndReasonName(endReason),
            BytesServed = bytesServed,
            Message = message,
            ConnectionWaitMs = stalls.ConnectionWaitMs,
            ProviderWaitMs = stalls.ProviderWaitMs,
            BodyDrainMs = stalls.BodyDrainMs,
            ConsumerWaitMs = stalls.ConsumerWaitMs,
            ClientWriteMs = stalls.ClientWriteMs,
            ConnectionsOpened = stalls.ConnectionsOpened,
            ConnectionsReused = stalls.ConnectionsReused,
        });
    }

    public IReadOnlyList<StreamTraceSessionSummary> ListSessions(int limit = 50)
    {
        return _sessions.Values
            .OrderByDescending(s => s.LastAt)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(s => new StreamTraceSessionSummary(
                s.SessionId,
                s.Path,
                s.FirstAt,
                s.LastAt,
                s.EventCount,
                s.LastKind))
            .ToList();
    }

    public IReadOnlyList<StreamTraceEvent> GetSessionEvents(Guid sessionId)
    {
        if (!Enabled && _buffer.Length == 0) return [];

        StreamTraceEvent?[] copy;
        lock (_gate)
        {
            copy = new StreamTraceEvent?[_buffer.Length];
            _buffer.CopyTo(copy, 0);
        }

        return copy
            .Where(e => e is not null && e.SessionId == sessionId)
            .OrderBy(e => e!.Sequence)
            .Select(e => e!)
            .ToList();
    }

    /// <summary>
    /// Newest <paramref name="limit"/> events across all sessions, oldest-first within
    /// the returned window. Used by the support pack exporter.
    /// </summary>
    public IReadOnlyList<StreamTraceEvent> GetRecentEvents(int limit)
    {
        StreamTraceEvent?[] copy;
        lock (_gate)
        {
            if (_buffer.Length == 0) return [];
            copy = new StreamTraceEvent?[_buffer.Length];
            _buffer.CopyTo(copy, 0);
        }

        return copy
            .Where(e => e is not null)
            .OrderByDescending(e => e!.Sequence)
            .Take(Math.Clamp(limit, 1, UiMaxCapacity))
            .OrderBy(e => e!.Sequence)
            .Select(e => e!)
            .ToList();
    }

    /// <summary>
    /// Serialize recent events as newline-delimited JSON for the support pack.
    /// </summary>
    public string FormatEventsJsonl(int limit)
    {
        var events = GetRecentEvents(limit);
        if (events.Count == 0) return "";
        return string.Join('\n', events.Select(e => JsonSerializer.Serialize(e, CompactJson))) + "\n";
    }

    private void TrimSessionsIfNeeded()
    {
        if (_sessions.Count <= _maxSessions) return;
        var excess = _sessions.Values
            .OrderBy(s => s.LastAt)
            .Take(_sessions.Count - _maxSessions)
            .Select(s => s.SessionId)
            .ToList();
        foreach (var id in excess)
            _sessions.TryRemove(id, out _);
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private sealed class SessionMeta
    {
        public Guid SessionId { get; init; }
        public long FirstAt { get; set; }
        public long LastAt { get; set; }
        public string? Path { get; set; }
        public int EventCount { get; set; }
        public string? LastKind { get; set; }
        public StallTotals Stalls { get; } = new();
    }

    /// <summary>
    /// Per-range stall accumulator. Written concurrently by every prefetch task and the
    /// consumer, so every field moves through Interlocked; drained and zeroed when the
    /// range ends so the next range on the same session starts clean.
    /// </summary>
    private sealed class StallTotals
    {
        private long _connectionWaitTicks;
        private long _providerWaitTicks;
        private long _bodyDrainTicks;
        private long _consumerWaitTicks;
        private long _clientWriteTicks;
        private long _connectionsOpened;
        private long _connectionsReused;

        public void Add(StreamStallKind kind, long ticks)
        {
            switch (kind)
            {
                case StreamStallKind.ConnectionWait:
                    Interlocked.Add(ref _connectionWaitTicks, ticks);
                    break;
                case StreamStallKind.ProviderWait:
                    Interlocked.Add(ref _providerWaitTicks, ticks);
                    break;
                case StreamStallKind.BodyDrain:
                    Interlocked.Add(ref _bodyDrainTicks, ticks);
                    break;
                case StreamStallKind.ConsumerWait:
                    Interlocked.Add(ref _consumerWaitTicks, ticks);
                    break;
                case StreamStallKind.ClientWrite:
                    Interlocked.Add(ref _clientWriteTicks, ticks);
                    break;
            }
        }

        public void AddConnection(long waitTicks, bool wasReused)
        {
            if (waitTicks > 0) Interlocked.Add(ref _connectionWaitTicks, waitTicks);
            if (wasReused)
                Interlocked.Increment(ref _connectionsReused);
            else
                Interlocked.Increment(ref _connectionsOpened);
        }

        public StallSnapshot DrainForRange() => new(
            Milliseconds(ref _connectionWaitTicks),
            Milliseconds(ref _providerWaitTicks),
            Milliseconds(ref _bodyDrainTicks),
            Milliseconds(ref _consumerWaitTicks),
            Milliseconds(ref _clientWriteTicks),
            Count(ref _connectionsOpened),
            Count(ref _connectionsReused));

        private static long? Milliseconds(ref long ticks)
        {
            var drained = Interlocked.Exchange(ref ticks, 0);
            return drained <= 0 ? null : drained / TimeSpan.TicksPerMillisecond;
        }

        private static long? Count(ref long counter)
        {
            var drained = Interlocked.Exchange(ref counter, 0);
            return drained <= 0 ? null : drained;
        }
    }

    private readonly record struct StallSnapshot(
        long? ConnectionWaitMs,
        long? ProviderWaitMs,
        long? BodyDrainMs,
        long? ConsumerWaitMs,
        long? ClientWriteMs,
        long? ConnectionsOpened,
        long? ConnectionsReused);
}

public sealed record StreamTraceSessionSummary(
    Guid SessionId,
    string? Path,
    long FirstAt,
    long LastAt,
    int EventCount,
    string? LastKind);
