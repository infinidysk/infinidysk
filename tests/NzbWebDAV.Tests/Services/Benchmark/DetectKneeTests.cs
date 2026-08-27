using NzbWebDAV.Services.Benchmark;

namespace NzbWebDAV.Tests.Services.Benchmark;

public class DetectKneeTests
{
    [Fact]
    public void DetectKnee_FindsPlateau()
    {
        var sweep = Sweep((1, 10), (2, 20), (4, 35), (8, 40), (16, 41));

        var knee = UsenetBenchmarkService.DetectKnee(sweep, null, []);

        Assert.Equal(8, knee);
    }

    [Fact]
    public void DetectKnee_SoftensSinglePeakSpike()
    {
        var sweep = Sweep((1, 30), (2, 38), (4, 40), (8, 41), (16, 46));

        var knee = UsenetBenchmarkService.DetectKnee(sweep, null, []);

        Assert.Equal(8, knee);
    }

    [Fact]
    public void DetectKnee_ClampsToProviderCap()
    {
        var sweep = Sweep((1, 10), (2, 20), (4, 35), (8, 40), (16, 41));

        var knee = UsenetBenchmarkService.DetectKnee(sweep, providerCap: 4, []);

        Assert.Equal(4, knee);
    }

    [Fact]
    public void DetectKnee_EmptySweepReturnsNull()
    {
        Assert.Null(UsenetBenchmarkService.DetectKnee([], null, []));
    }

    [Fact]
    public void DetectKnee_AllZeroSpeedsReturnsNull()
    {
        var sweep = Sweep((2, 0), (4, 0), (8, 0));
        var warnings = new List<string>();

        var knee = UsenetBenchmarkService.DetectKnee(sweep, null, warnings);

        Assert.Null(knee);
        Assert.Contains(warnings, warning => warning.Contains("couldn't get steady throughput", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DetectKnee_MostlyZeroSweepWithCeilingSpikeReturnsNull()
    {
        var sweep = Sweep(
            (1, 0), (2, 0), (4, 0), (8, 0), (16, 0), (32, 0), (50, 88));
        var warnings = new List<string>();

        var knee = UsenetBenchmarkService.DetectKnee(sweep, null, warnings);

        Assert.Null(knee);
        Assert.Contains(warnings, warning => warning.Contains("couldn't get steady throughput", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DetectKnee_LoneSpikeAboveTinySecondBestReturnsNull()
    {
        // Matches the laptop failure mode: zeros, a blip at 40, then a ceiling spike.
        var sweep = Sweep(
            (1, 0), (8, 0), (16, 0), (32, 0), (40, 3.5), (50, 105));
        var warnings = new List<string>();

        Assert.Null(UsenetBenchmarkService.DetectKnee(sweep, null, warnings));
    }

    [Fact]
    public void DetectKnee_AddsWarningForNoisyMeasurement()
    {
        var sweep = Sweep((1, 10), (2, 20), (4, 21));
        sweep[1].Cv = 0.3;
        var warnings = new List<string>();

        UsenetBenchmarkService.DetectKnee(sweep, null, warnings);

        Assert.Contains(warnings, warning => warning.Contains("noisy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DetectKnee_SetsStillClimbingWhenPeakKeepsRising()
    {
        var sweep = Sweep((1, 10), (2, 20), (4, 40), (8, 80), (16, 120));
        var warnings = new List<string>();

        var knee = UsenetBenchmarkService.DetectKnee(sweep, null, warnings, out var stillClimbing);

        Assert.Equal(16, knee);
        Assert.True(stillClimbing);
        Assert.Contains(warnings, warning => warning.Contains("still climbing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DetectKnee_StillClimbingFalseOnPlateau()
    {
        var sweep = Sweep((1, 10), (2, 20), (4, 35), (8, 40), (16, 41));

        UsenetBenchmarkService.DetectKnee(sweep, null, [], out var stillClimbing);

        Assert.False(stillClimbing);
    }

    [Fact]
    public void CapTransferRecommendation_LeavesRecommendationWithinProviderLimit()
    {
        var warnings = new List<string>();

        var recommendation = UsenetBenchmarkService.CapTransferRecommendation(20, 50, warnings);

        Assert.Equal(20, recommendation);
        Assert.Empty(warnings);
    }

    [Fact]
    public void CapTransferRecommendation_DoesNotRecommendChangingProviderLimit()
    {
        var warnings = new List<string>();

        var recommendation = UsenetBenchmarkService.CapTransferRecommendation(40, 20, warnings);

        Assert.Equal(20, recommendation);
        var warning = Assert.Single(warnings);
        Assert.Contains("Provider Connection Limit (20)", warning, StringComparison.Ordinal);
        Assert.Contains("re-run Auto-tune", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void CapTransferRecommendation_DoesNotDuplicateWarningOnConfirmationRun()
    {
        var warnings = new List<string>();

        UsenetBenchmarkService.CapTransferRecommendation(40, 20, warnings);
        UsenetBenchmarkService.CapTransferRecommendation(35, 20, warnings);

        Assert.Single(warnings);
    }

    [Fact]
    public void CapTransferRecommendation_PreservesMissingRecommendation()
    {
        Assert.Null(UsenetBenchmarkService.CapTransferRecommendation(null, 20));
    }

    private static List<BenchmarkSweepPoint> Sweep(params (int Connections, double MegaBytesPerSec)[] points) =>
        points.Select(point => new BenchmarkSweepPoint
        {
            Connections = point.Connections,
            MegaBytesPerSec = point.MegaBytesPerSec,
        }).ToList();
}
