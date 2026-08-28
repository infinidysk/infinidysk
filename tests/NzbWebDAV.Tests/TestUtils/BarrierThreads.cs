using System.Diagnostics.CodeAnalysis;

namespace NzbWebDAV.Tests.TestUtils;

internal static class BarrierThreads
{
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Worker exceptions must be marshalled to the test thread instead of terminating the host.")]
    public static void Run(int count, Action<int> body, TimeSpan? joinTimeout = null)
    {
        var timeout = joinTimeout ?? TimeSpan.FromSeconds(10);
        var exceptions = new Exception?[count];
        using var barrier = new Barrier(count);
        var threads = new Thread[count];
        for (var i = 0; i < count; i++)
        {
            var index = i;
            threads[i] = new Thread(() =>
            {
                try
                {
                    barrier.SignalAndWait();
                    body(index);
                }
                catch (Exception ex)
                {
                    exceptions[index] = ex;
                }
            })
            {
                IsBackground = true,
            };
            threads[i].Start();
        }

        var timedOut = false;
        foreach (var thread in threads)
        {
            if (!thread.Join(timeout))
                timedOut = true;
        }

        var failures = exceptions.OfType<Exception>().ToArray();
        if (failures.Length > 0)
            throw new AggregateException(failures);
        if (timedOut)
            throw new TimeoutException($"Barrier worker did not complete within {timeout}.");
    }
}
