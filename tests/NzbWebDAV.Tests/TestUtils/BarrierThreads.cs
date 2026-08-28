namespace NzbWebDAV.Tests.TestUtils;

internal static class BarrierThreads
{
    public static void Run(int count, Action<int> body, TimeSpan? joinTimeout = null)
    {
        var timeout = joinTimeout ?? TimeSpan.FromSeconds(10);
        using var barrier = new Barrier(count);
        var threads = new Thread[count];
        for (var i = 0; i < count; i++)
        {
            var index = i;
            threads[i] = new Thread(() =>
            {
                barrier.SignalAndWait();
                body(index);
            })
            {
                IsBackground = true,
            };
            threads[i].Start();
        }

        foreach (var thread in threads)
        {
            if (!thread.Join(timeout))
                throw new TimeoutException($"Barrier worker did not complete within {timeout}.");
        }
    }
}
