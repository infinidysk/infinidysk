using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace SharpCompress.Compressors.ZStandard;

internal unsafe class JobThreadPool : IDisposable
{
    private int numThreads;
    private readonly List<JobThread> threads;
    private readonly BlockingCollection<Job> queue;

    private struct Job
    {
        public void* function;
        public void* opaque;
    }

    private class JobThread
    {
        private Thread Thread { get; }
        public CancellationTokenSource CancellationTokenSource { get; }

        public JobThread(Thread thread)
        {
            CancellationTokenSource = new CancellationTokenSource();
            Thread = thread;
        }

        public void Start()
        {
            Thread.Start(this);
        }

        public void Cancel()
        {
            CancellationTokenSource.Cancel();
        }

        public void Join()
        {
            Thread.Join();
        }
    }

    private void Worker(object? obj)
    {
        if (obj is not JobThread poolThread)
        {
            return;
        }

        var cancellationToken = poolThread.CancellationTokenSource.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            Job job;
            try
            {
                // TryTake returns false once CompleteAdding has been called and the queue drains.
                if (!queue.TryTake(out job, -1, cancellationToken))
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                // Pool shutdown via Cancel(); expected during Join/Dispose.
                return;
            }
            catch (InvalidOperationException)
            {
                // Race with CompleteAdding marking the collection complete mid-take.
                return;
            }

            // Deliberately outside the try: a throwing job must fail visibly
            // (unhandled thread exception) rather than be swallowed and deadlock the writer.
            ((delegate* managed<void*, void>)job.function)(job.opaque);
        }
    }

    public JobThreadPool(int num, int queueSize)
    {
        numThreads = num;
        queue = new BlockingCollection<Job>(queueSize + 1);
        threads = new List<JobThread>(num);
        for (var i = 0; i < numThreads; i++)
        {
            CreateThread();
        }
    }

    private void CreateThread()
    {
        var poolThread = new JobThread(new Thread(Worker));
        threads.Add(poolThread);
        poolThread.Start();
    }

    public void Resize(int num)
    {
        lock (threads)
        {
            if (num < numThreads)
            {
                for (var i = numThreads - 1; i >= num; i--)
                {
                    threads[i].Cancel();
                    threads.RemoveAt(i);
                }
            }
            else
            {
                for (var i = numThreads; i < num; i++)
                {
                    CreateThread();
                }
            }
        }

        numThreads = num;
    }

    public void Add(void* function, void* opaque)
    {
        queue.Add(new Job { function = function, opaque = opaque });
    }

    public bool TryAdd(void* function, void* opaque)
    {
        return queue.TryAdd(new Job { function = function, opaque = opaque });
    }

    public void Join(bool cancel = true)
    {
        queue.CompleteAdding();
        List<JobThread> jobThreads;
        lock (threads)
        {
            jobThreads = new List<JobThread>(threads);
        }

        if (cancel)
        {
            foreach (var thread in jobThreads)
            {
                thread.Cancel();
            }
        }

        foreach (var thread in jobThreads)
        {
            thread.Join();
        }
    }

    public void Dispose()
    {
        queue.Dispose();
    }

    public int Size()
    {
        // Memory-usage estimate for POOL_sizeof / ZSTDMT_sizeof_CCtx only.
        // Returning 0 underreports that estimate; it is not a correctness issue.
        // Accurate managed-heap sizing for Thread/BlockingCollection is not
        // available (see https://github.com/dotnet/runtime/issues/24200).
        return 0;
    }
}
