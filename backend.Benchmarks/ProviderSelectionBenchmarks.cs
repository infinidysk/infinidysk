using BenchmarkDotNet.Attributes;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Services.Metrics;

namespace NzbWebDAV.Benchmarks;

[MemoryDiagnoser]
public class ProviderSelectionBenchmarks : IDisposable
{
    private CancellationTokenSource _activityCancellation = null!;
    private MultiProviderNntpClient _router = null!;
    private Task[] _activityTasks = null!;

    [Params(false, true)]
    public bool Cascade { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var providers = Enumerable.Range(0, 8)
            .Select(index =>
            {
                var pool = new ConnectionPool<INntpClient>(
                    maxConnections: 50,
                    _ => throw new InvalidOperationException(
                        "Provider selection must not create a physical connection."));
                return new MultiConnectionNntpClient(
                    pool,
                    ProviderType.Pooled,
                    new ProviderCircuitBreaker($"selection-{index}"),
                    $"selection-{index}",
                    priority: index % 3,
                    metricsKey: $"selection-{index}",
                    maxTransferConnections: 20);
            })
            .ToList();

        _router = new MultiProviderNntpClient(
            providers,
            cascadeEnabled: () => Cascade);
        _activityCancellation = new CancellationTokenSource();
        _activityTasks = providers
            .Select(provider => Task.Run(
                () => ChurnMetadataAdmissionAsync(
                    provider,
                    _activityCancellation.Token)))
            .ToArray();
    }

    [Benchmark(Baseline = true)]
    public string? SelectMetadataProvider() =>
        _router.SelectProviderForBenchmark(NntpOperation.Stat)?.MetricsKey;

    [Benchmark]
    public string? SelectTransferProvider() =>
        _router.SelectProviderForBenchmark(NntpOperation.Body)?.MetricsKey;

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing || _activityCancellation is null)
            return;

        _activityCancellation.Cancel();
        try
        {
            Task.WhenAll(_activityTasks).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Expected benchmark teardown.
        }
        finally
        {
            _router.Dispose();
            _activityCancellation.Dispose();
            _activityCancellation = null!;
        }
    }

    private static async Task ChurnMetadataAdmissionAsync(
        MultiConnectionNntpClient provider,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var lease = await provider.AcquireKeepAliveAdmissionAsync(
                        cancellationToken)
                    .ConfigureAwait(false);
                await Task.Yield();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected benchmark teardown.
        }
    }
}
