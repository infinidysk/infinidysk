using System.Threading.Channels;
using Serilog.Core;
using Serilog.Events;

namespace NzbWebDAV.Logging;

/// <summary>
/// Thread-safe in-memory ring buffer that doubles as a Serilog sink. Stores the
/// most recent N entries (default 2000, configurable via LOG_BUFFER_SIZE) with a
/// monotonic sequence number, and pushes each new entry onto a bounded channel
/// for downstream consumers (the WebSocket broadcaster). Drops oldest channel
/// entries under back-pressure so logging never blocks application code.
/// </summary>
public sealed class LogBufferSink : ILogEventSink
{
    private readonly int _capacity;
    private readonly LogEntry?[] _buffer;
    private readonly object _gate = new();
    private long _nextSequence;
    private readonly Channel<LogEntry> _channel;

    public LogBufferSink(int capacity)
    {
        _capacity = capacity;
        _buffer = new LogEntry?[capacity];
        _channel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public ChannelReader<LogEntry> StreamReader => _channel.Reader;

    public int Capacity => _capacity;

    public void Emit(LogEvent logEvent)
    {
        var sequence = Interlocked.Increment(ref _nextSequence);
        var entry = ToEntry(sequence, logEvent);
        lock (_gate)
        {
            _buffer[(sequence - 1) % _capacity] = entry;
        }
        _channel.Writer.TryWrite(entry);
    }

    /// <summary>
    /// Returns the most-recent entries matching the supplied filters, ordered
    /// ascending by sequence (oldest first). Also returns per-level totals
    /// across the entire buffer (unfiltered) so the UI can render badge counts.
    /// </summary>
    public LogSnapshot Snapshot(
        int limit,
        IReadOnlyCollection<string>? levels,
        string? source,
        string? search,
        long? beforeSequence)
    {
        LogEntry[] copy;
        long oldest;
        long newest;
        lock (_gate)
        {
            copy = new LogEntry[_capacity];
            _buffer.CopyTo(copy, 0);
            newest = _nextSequence;
            oldest = Math.Max(1, newest - _capacity + 1);
        }

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var matches = new List<LogEntry>(Math.Min(limit, _capacity));

        for (var i = 0; i < copy.Length; i++)
        {
            var entry = copy[i];
            if (entry is null) continue;
            counts[entry.Level] = counts.GetValueOrDefault(entry.Level) + 1;
        }

        foreach (var entry in copy
                     .Where(x => x is not null)
                     .OrderByDescending(x => x!.Sequence))
        {
            if (entry is null) continue;
            if (beforeSequence is { } before && entry.Sequence >= before) continue;
            if (levels is { Count: > 0 } && !levels.Contains(entry.Level, StringComparer.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(source) &&
                (entry.Source is null ||
                 entry.Source.IndexOf(source, StringComparison.OrdinalIgnoreCase) < 0)) continue;
            if (!string.IsNullOrEmpty(search) && !MatchesSearch(entry, search)) continue;

            matches.Add(entry);
            if (matches.Count >= limit) break;
        }

        matches.Reverse();
        return new LogSnapshot(matches, counts, oldest, newest);
    }

    private static bool MatchesSearch(LogEntry entry, string search)
    {
        if (entry.Message.Contains(search, StringComparison.OrdinalIgnoreCase)) return true;
        if (entry.Exception is { } ex && ex.Contains(search, StringComparison.OrdinalIgnoreCase)) return true;
        if (entry.Source is { } src && src.Contains(search, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static LogEntry ToEntry(long sequence, LogEvent e)
    {
        string? source = null;
        if (e.Properties.TryGetValue("SourceContext", out var sc) && sc is ScalarValue { Value: string s })
            source = s;

        return new LogEntry
        {
            Sequence = sequence,
            TimestampUnixMs = e.Timestamp.ToUnixTimeMilliseconds(),
            Level = e.Level.ToString(),
            Message = e.RenderMessage(),
            Source = source,
            Exception = e.Exception?.ToString(),
        };
    }
}

public sealed record LogSnapshot(
    IReadOnlyList<LogEntry> Entries,
    IReadOnlyDictionary<string, int> CountsByLevel,
    long OldestSequence,
    long NewestSequence);
