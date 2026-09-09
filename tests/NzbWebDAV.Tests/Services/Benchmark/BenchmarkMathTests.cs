using NzbWebDAV.Services.Benchmark;

namespace NzbWebDAV.Tests.Services.Benchmark;

public class BenchmarkMathTests
{
    [Fact]
    public void NormalizeUnavailableCorpusResult_ClearsThroughputClaims()
    {
        var pool = new BenchmarkSegmentPool(["missing"]);
        pool.MarkDead("missing");
        var result = new BenchmarkResult
        {
            ThroughputTested = true,
            RecommendedConnections = 8,
            Pipelining = new BenchmarkPipelining(),
            WrappedPool = true,
            StillClimbing = true,
            Sweep = [new BenchmarkSweepPoint { Connections = 1, MegaBytesPerSec = 0 }],
        };

        UsenetBenchmarkService.NormalizeUnavailableCorpusResult(result, pool);

        Assert.False(result.ThroughputTested);
        Assert.Empty(result.Sweep);
        Assert.Null(result.RecommendedConnections);
        Assert.Null(result.Pipelining);
        Assert.False(result.WrappedPool);
        Assert.False(result.StillClimbing);
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("provider-retrievable", StringComparison.Ordinal));
    }

    [Fact]
    public void NormalizeUnavailableCorpusResult_KeepsResultsWhenDataMoved()
    {
        var pool = new BenchmarkSegmentPool(["retrieved"]);
        var result = new BenchmarkResult
        {
            ThroughputTested = true,
            DataUsedBytes = 1,
            RecommendedConnections = 8,
            Sweep = [new BenchmarkSweepPoint { Connections = 1, MegaBytesPerSec = 40 }],
        };

        UsenetBenchmarkService.NormalizeUnavailableCorpusResult(result, pool);

        Assert.True(result.ThroughputTested);
        Assert.Equal(8, result.RecommendedConnections);
        Assert.Single(result.Sweep);
    }

    [Fact]
    public void NormalizeUnavailableCorpusResult_ReportsNoDataWhenPoolIsNotExhausted()
    {
        var pool = new BenchmarkSegmentPool(["available"]);
        var result = new BenchmarkResult { ThroughputTested = true };

        UsenetBenchmarkService.NormalizeUnavailableCorpusResult(result, pool);

        Assert.False(result.ThroughputTested);
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("did not receive any article data", StringComparison.Ordinal));
    }

    [Fact]
    public void ComputeSteadyRate_UsesMedianOfBuckets()
    {
        var buckets = new List<(long Bytes, double Seconds)>
        {
            (10_000_000, 0.5),
            (12_000_000, 0.5),
            (11_000_000, 0.5),
        };

        var (megaBytesPerSec, cv) = UsenetBenchmarkService.ComputeSteadyRate(buckets, 0, 0);

        Assert.Equal(22, megaBytesPerSec, 6);
        Assert.InRange(cv, 0.07, 0.08);
    }

    [Fact]
    public void ComputeSteadyRate_IdenticalBucketsHaveZeroVariation()
    {
        var buckets = Enumerable.Repeat((Bytes: 10_000_000L, Seconds: 0.5), 3).ToList();

        var (_, cv) = UsenetBenchmarkService.ComputeSteadyRate(buckets, 0, 0);

        Assert.Equal(0, cv);
    }

    [Fact]
    public void ComputeSteadyRate_FallsBackWhenThereAreTooFewBuckets()
    {
        var buckets = new List<(long Bytes, double Seconds)>
        {
            (10_000_000, 0.5),
            (12_000_000, 0.5),
        };

        var (megaBytesPerSec, cv) = UsenetBenchmarkService.ComputeSteadyRate(
            buckets, fallbackBytes: 9_000_000, fallbackSeconds: 0.5);

        Assert.Equal(18, megaBytesPerSec, 6);
        Assert.True(cv >= 0.5);
    }

    [Fact]
    public void ComputeSteadyRate_IgnoresEmptyBucketsWhenComputingMedian()
    {
        var buckets = new List<(long Bytes, double Seconds)>
        {
            (10_000_000, 0.5),
            (0, 0.5),
            (0, 0.5),
            (0, 0.5),
            (12_000_000, 0.5),
            (11_000_000, 0.5),
        };

        var (megaBytesPerSec, cv) = UsenetBenchmarkService.ComputeSteadyRate(
            buckets, fallbackBytes: 33_000_000, fallbackSeconds: 3.0);

        Assert.Equal(22, megaBytesPerSec, 6);
        Assert.InRange(cv, 0.07, 0.08);
    }

    [Fact]
    public void ComputeSteadyRate_UsesWindowMeanWhenMedianIsZeroButBytesMoved()
    {
        // Three tiny positive rates would be needed for median path; with only
        // empty+one positive bucket we fall back. Force the median≈0 + bytes path
        // with three near-zero positive buckets and a large window mean.
        var buckets = new List<(long Bytes, double Seconds)>
        {
            (1_000, 0.5),
            (1_000, 0.5),
            (1_000, 0.5),
        };

        var (megaBytesPerSec, cv) = UsenetBenchmarkService.ComputeSteadyRate(
            buckets, fallbackBytes: 30_000_000, fallbackSeconds: 1.5);

        Assert.Equal(20, megaBytesPerSec, 6);
        Assert.Equal(0.5, cv);
    }

    [Fact]
    public void ComputeSteadyRate_ZeroDurationFallbackReturnsZero()
    {
        var (megaBytesPerSec, cv) = UsenetBenchmarkService.ComputeSteadyRate(
            [], fallbackBytes: 9_000_000, fallbackSeconds: 0);

        Assert.Equal(0, megaBytesPerSec);
        Assert.Equal(1, cv);
    }

    [Fact]
    public void AdaptiveTargetBytes_UsesConnectionScaledBootstrapWithoutEstimate()
    {
        var profile = BenchmarkProfile.For(BenchmarkIntensity.Quick);

        var one = UsenetBenchmarkService.AdaptiveTargetBytes(0, profile, 500_000_000, connections: 1);
        var eight = UsenetBenchmarkService.AdaptiveTargetBytes(0, profile, 500_000_000, connections: 8);

        Assert.True(one >= profile.PerLevelBytes);
        Assert.True(eight > one);
        Assert.Equal(128_000_000, eight); // 8 conn × 2 MB/s × 2 × 4s
    }

    [Fact]
    public void AdaptiveTargetBytes_ScalesFastEstimateAcrossFullWindow()
    {
        var profile = BenchmarkProfile.For(BenchmarkIntensity.Quick);

        var bytes = UsenetBenchmarkService.AdaptiveTargetBytes(10, profile, 500_000_000);

        Assert.Equal(80_000_000, bytes);
    }

    [Fact]
    public void AdaptiveTargetBytes_HandlesBudgetBelowProfileFloor()
    {
        var profile = BenchmarkProfile.For(BenchmarkIntensity.Quick);

        var exception = Record.Exception(
            () => UsenetBenchmarkService.AdaptiveTargetBytes(10, profile, 5_000_000));
        var bytes = UsenetBenchmarkService.AdaptiveTargetBytes(10, profile, 5_000_000);

        Assert.Null(exception);
        Assert.Equal(5_000_000, bytes);
    }

    [Fact]
    public void AdaptiveTargetBytes_RespectsRemainingBudget()
    {
        var profile = BenchmarkProfile.For(BenchmarkIntensity.Thorough);

        var bytes = UsenetBenchmarkService.AdaptiveTargetBytes(100, profile, 75_000_000);

        Assert.Equal(75_000_000, bytes);
    }

    [Fact]
    public void MinUsefulBytes_HasFloorAndScalesWithSpeed()
    {
        var profile = BenchmarkProfile.For(BenchmarkIntensity.Quick);

        Assert.Equal(4_000_000, UsenetBenchmarkService.MinUsefulBytes(0.5, profile));
        Assert.Equal(25_000_000, UsenetBenchmarkService.MinUsefulBytes(10, profile));
    }

    [Fact]
    public void PipeliningReserveBytes_ScalesWithSpeedAndCapsAtQuarterBudget()
    {
        var profile = BenchmarkProfile.For(BenchmarkIntensity.Thorough);

        var reserve = UsenetBenchmarkService.PipeliningReserveBytes(profile, 100, 10_000_000_000);
        // 4 steps (baseline + 3 depths) × MinUseful(100) = 4 × 300MB = 1.2GB, under 25% of 10GB
        Assert.Equal(1_200_000_000, reserve);

        var capped = UsenetBenchmarkService.PipeliningReserveBytes(profile, 100, 1_000_000_000);
        Assert.Equal(250_000_000, capped);
    }

    [Theory]
    [InlineData(0.1, false, false, false, true, "high")]
    [InlineData(0.2, false, false, false, true, "medium")]
    [InlineData(0.1, true, false, false, true, "medium")]
    [InlineData(0.35, false, false, false, true, "low")]
    [InlineData(0.1, false, true, true, true, "low")]
    [InlineData(0.1, false, true, false, true, "medium")]
    [InlineData(0.1, false, false, false, false, "low")]
    public void ComputeConfidence_ReflectsMeasurementQuality(
        double cv,
        bool wrappedPool,
        bool budgetLimited,
        bool stillClimbing,
        bool throughputTested,
        string expected)
    {
        var result = new BenchmarkResult
        {
            ThroughputTested = throughputTested,
            WrappedPool = wrappedPool,
            BudgetLimited = budgetLimited,
            StillClimbing = stillClimbing,
            RecommendedConnections = 1,
            Sweep = [new BenchmarkSweepPoint { Connections = 1, MegaBytesPerSec = 10, Cv = cv }],
        };

        Assert.Equal(expected, UsenetBenchmarkService.ComputeConfidence(result));
    }

    [Fact]
    public void ComputeConfidence_IgnoresNoisyLowConnectionPointNearKnee()
    {
        var result = new BenchmarkResult
        {
            ThroughputTested = true,
            RecommendedConnections = 16,
            Sweep =
            [
                new BenchmarkSweepPoint { Connections = 1, MegaBytesPerSec = 10, Cv = 0.8 },
                new BenchmarkSweepPoint { Connections = 8, MegaBytesPerSec = 80, Cv = 0.05 },
                new BenchmarkSweepPoint { Connections = 16, MegaBytesPerSec = 100, Cv = 0.08 },
            ],
        };

        Assert.Equal("high", UsenetBenchmarkService.ComputeConfidence(result));
    }

    [Fact]
    public void ComputeConfidence_TightConfirmOverridesWrappedPoolCap()
    {
        var result = new BenchmarkResult
        {
            ThroughputTested = true,
            WrappedPool = true,
            ConfirmDeltaPct = 3,
            RecommendedConnections = 8,
            Sweep = [new BenchmarkSweepPoint { Connections = 8, MegaBytesPerSec = 40, Cv = 0.05 }],
        };

        Assert.Equal("high", UsenetBenchmarkService.ComputeConfidence(result));
    }

    [Fact]
    public void ComputeConfidence_LooseConfirmDowngradesOneLevel()
    {
        var result = new BenchmarkResult
        {
            ThroughputTested = true,
            ConfirmDeltaPct = 25,
            RecommendedConnections = 8,
            Sweep = [new BenchmarkSweepPoint { Connections = 8, MegaBytesPerSec = 40, Cv = 0.05 }],
        };

        Assert.Equal("medium", UsenetBenchmarkService.ComputeConfidence(result));
    }

    [Fact]
    public void ComputeConfidence_BudgetLimitedWithoutClimbingCapsAtMedium()
    {
        var result = new BenchmarkResult
        {
            ThroughputTested = true,
            BudgetLimited = true,
            StillClimbing = false,
            RecommendedConnections = 8,
            Sweep =
            [
                new BenchmarkSweepPoint { Connections = 4, MegaBytesPerSec = 38, Cv = 0.05 },
                new BenchmarkSweepPoint { Connections = 8, MegaBytesPerSec = 40, Cv = 0.05 },
            ],
        };

        Assert.Equal("medium", UsenetBenchmarkService.ComputeConfidence(result));
    }
}
