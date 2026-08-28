using BenchmarkDotNet.Attributes;
using NzbWebDAV.Streams;

namespace NzbWebDAV.Benchmarks;

[MemoryDiagnoser]
public class SegmentBufferPoolRetentionBenchmarks
{
    private const int BurstCount = 65;
    private const int RepeatRounds = 3;
    private const int Size256 = 256 * 1024;
    private const int Size512 = 512 * 1024;
    private const int Size768 = 768 * 1024;
    private static readonly int[] MixedSizes = [Size256, Size512, Size768];

    private int[] _burstSchedule = null!;
    private int[] _mixedSchedule = null!;
    private BenchmarkManualTimeProvider _clock = null!;
    private SegmentBufferPool _pool = null!;
    private long _warmAllocationCount;

    public enum BenchmarkRetentionPolicy
    {
        Legacy,
        CapacityOnly,
    }

    [Params(BenchmarkRetentionPolicy.Legacy, BenchmarkRetentionPolicy.CapacityOnly)]
    public BenchmarkRetentionPolicy Policy { get; set; }

    [Params(1, 8)]
    public int ConcurrentStreams { get; set; }

    private SegmentBufferRetentionPolicy RuntimePolicy => Policy switch
    {
        BenchmarkRetentionPolicy.Legacy => SegmentBufferRetentionPolicy.Legacy,
        BenchmarkRetentionPolicy.CapacityOnly => SegmentBufferRetentionPolicy.CapacityOnly,
        _ => throw new ArgumentOutOfRangeException(nameof(Policy)),
    };

    [GlobalSetup]
    public void GlobalSetup()
    {
        _burstSchedule = Enumerable.Repeat(Size256, BurstCount).ToArray();
        var random = new Random(42);
        _mixedSchedule = Enumerable.Range(0, 24)
            .Select(_ => MixedSizes[random.Next(MixedSizes.Length)])
            .ToArray();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        const long maxIdleBytes = 128L * 1024 * 1024;
        _clock = new BenchmarkManualTimeProvider();
        _pool = new SegmentBufferPool(
            maxIdleBytes,
            RuntimePolicy,
            timeProvider: _clock);
        RunBurst(_pool, _burstSchedule);
        RunBurst(_pool, _mixedSchedule);
        _warmAllocationCount = _pool.Snapshot().AllocationCount;
    }

    [Benchmark]
    public long BurstGapRepeat()
    {
        for (var round = 0; round < RepeatRounds; round++)
        {
            _clock.Advance(TimeSpan.FromMinutes(3));
            if (ConcurrentStreams == 1)
            {
                RunBurst(_pool, _burstSchedule);
                RunBurst(_pool, _mixedSchedule);
            }
            else
            {
                RunConcurrentBurst(_pool, _burstSchedule, ConcurrentStreams);
                RunConcurrentBurst(_pool, _mixedSchedule, ConcurrentStreams);
            }
        }

        var snapshot = _pool.Snapshot();
        if (snapshot.IdleBytes > snapshot.MaxIdleBytes)
        {
            throw new InvalidOperationException(
                $"IdleBytes {snapshot.IdleBytes} exceeded maxIdleBytes {snapshot.MaxIdleBytes}.");
        }

        if (snapshot.CheckedOutBytes != 0 || snapshot.RentCount != snapshot.ReturnCount)
        {
            throw new InvalidOperationException(
                $"Rent/return imbalance: {snapshot.RentCount}/{snapshot.ReturnCount} outstanding {snapshot.CheckedOutBytes}.");
        }

        if (Policy == BenchmarkRetentionPolicy.CapacityOnly &&
            ConcurrentStreams == 1 &&
            snapshot.AllocationCount != _warmAllocationCount)
        {
            throw new InvalidOperationException(
                $"CapacityOnly allocated after warm-up: warm={_warmAllocationCount} final={snapshot.AllocationCount}.");
        }

        return snapshot.ReuseCount
               + snapshot.AllocationCount
               + snapshot.StaleExpiredBytes
               + snapshot.ClassLimitDroppedBytes
               + snapshot.CapacityEvictedBytes
               + snapshot.IdleBytes;
    }

    private static void RunBurst(SegmentBufferPool pool, int[] schedule)
    {
        var rented = new byte[schedule.Length][];
        for (var i = 0; i < schedule.Length; i++)
            rented[i] = pool.Rent(schedule[i]);
        foreach (var buffer in rented)
            pool.Return(buffer);
    }

    private static void RunConcurrentBurst(SegmentBufferPool pool, int[] schedule, int concurrency)
    {
        Parallel.For(0, concurrency, _ => RunBurst(pool, schedule));
    }

    private sealed class BenchmarkManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public override long GetTimestamp() => _now.UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
