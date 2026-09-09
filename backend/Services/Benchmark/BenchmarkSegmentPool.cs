using System.Collections.Concurrent;

namespace NzbWebDAV.Services.Benchmark;

internal sealed record BenchmarkSegment(string PrimaryId, IReadOnlyList<string> FallbackIds)
{
    public IReadOnlyList<string> CandidateIds { get; } = [PrimaryId, .. FallbackIds];
}

/// <summary>
/// Thread-safe round-robin view over logical benchmark segments for one run.
/// Definitive misses advance a segment to its next fallback Message-ID, and an
/// exhausted pool stops producing work instead of recycling known-dead IDs.
/// </summary>
internal sealed class BenchmarkSegmentPool(IReadOnlyList<BenchmarkSegment> segments)
{
    private readonly ConcurrentDictionary<string, byte> _dead = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _retrievals = new(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, string> _logicalSegmentIds = BuildLogicalSegmentIds(segments);
    private long _cursor = -1;

    public BenchmarkSegmentPool(IReadOnlyList<string> ids)
        : this(ids.Select(id => new BenchmarkSegment(id, [])).ToArray())
    {
    }

    public int Count => segments.Count;
    public int DeadCount => segments.Count(segment => segment.CandidateIds.All(_dead.ContainsKey));
    public bool Exhausted => DeadCount == Count;
    public bool WrappedAround => _retrievals.Values.Any(count => count > 1);

    public void MarkDead(string id) => _dead.TryAdd(id, 0);

    public void MarkRetrieved(string id)
    {
        var logicalId = _logicalSegmentIds.GetValueOrDefault(id, id);
        _retrievals.AddOrUpdate(logicalId, 1, (_, count) => count + 1);
    }

    public bool TryNext(out string id)
    {
        for (var attempt = 0; attempt < segments.Count; attempt++)
        {
            var index = Interlocked.Increment(ref _cursor);
            var segment = segments[(int)((index % segments.Count + segments.Count) % segments.Count)];
            var candidate = segment.CandidateIds.FirstOrDefault(candidateId => !_dead.ContainsKey(candidateId));
            if (candidate is not null)
            {
                id = candidate;
                return true;
            }
        }

        id = string.Empty;
        return false;
    }

    public List<string> NextBatch(int count)
    {
        var batch = new List<string>(count);
        for (var i = 0; i < count && TryNext(out var id); i++)
            batch.Add(id);
        return batch;
    }

    private static Dictionary<string, string> BuildLogicalSegmentIds(
        IReadOnlyList<BenchmarkSegment> source)
    {
        var logicalSegmentIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var segment in source)
        foreach (var id in segment.CandidateIds)
            logicalSegmentIds[id] = segment.PrimaryId;
        return logicalSegmentIds;
    }
}
