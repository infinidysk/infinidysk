using BenchmarkDotNet.Attributes;
using NzbWebDAV.Clients.Usenet.Connections;

namespace NzbWebDAV.Benchmarks;

[MemoryDiagnoser]
public class ProviderCircuitBreakerAcquisitionBenchmarks
{
    private const int OperationsPerRun = 4096;
    private ProviderCircuitBreaker _breaker = null!;

    [GlobalSetup]
    public void Setup()
    {
        _breaker = new ProviderCircuitBreaker("acquisition-benchmark");
    }

    [Benchmark(OperationsPerInvoke = OperationsPerRun)]
    public int BeginAndCommitClosedCircuit()
    {
        var committed = 0;
        for (var index = 0; index < OperationsPerRun; index++)
        {
            var acquisition = _breaker.BeginAcquisition(CircuitProbeLease.None);
            acquisition.Commit();
            if (acquisition.CircuitCancellationToken.IsCancellationRequested)
                throw new InvalidOperationException("Closed breaker epoch was cancelled.");
            committed++;
        }
        return committed;
    }
}
