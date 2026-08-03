using UsenetSharp.Concurrency;

namespace UsenetSharpTest.Concurrency;

[TestFixture]
public class AsyncSemaphoreTests
{
    [Test]
    public void Constructor_WithNegativeCount_ThrowsArgumentOutOfRangeException()
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => new AsyncSemaphore(-1));
    }

    [Test]
    public void Constructor_WithZeroCount_DoesNotThrow()
    {
        // Act & Assert
        Assert.DoesNotThrow(() => new AsyncSemaphore(0));
    }

    [Test]
    public void Constructor_WithPositiveCount_DoesNotThrow()
    {
        // Act & Assert
        Assert.DoesNotThrow(() => new AsyncSemaphore(5));
    }

    [Test]
    public async Task WaitAsync_WithAvailableCount_ReturnsImmediately()
    {
        // Arrange
        using var semaphore = new AsyncSemaphore(1);

        // Act
        var task = semaphore.WaitAsync();

        // Assert
        Assert.That(task.IsCompleted, Is.True, "Task should complete immediately when count > 0");
        await task; // Should not throw
    }

    [Test]
    public async Task WaitAsync_WithZeroCount_BlocksUntilRelease()
    {
        // Arrange
        using var semaphore = new AsyncSemaphore(0);

        // Act
        var waitTask = semaphore.WaitAsync();

        // Assert - Should not complete immediately
        await Task.Delay(50);
        Assert.That(waitTask.IsCompleted, Is.False, "Task should block when count is 0");

        // Release and verify completion
        semaphore.Release();
        await waitTask.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.That(waitTask.IsCompleted, Is.True, "Task should complete after Release()");
    }

    [Test]
    public async Task WaitAsync_MultipleTimes_DecrementsCount()
    {
        // Arrange
        using var semaphore = new AsyncSemaphore(3);

        // Act - First three waits should succeed immediately
        var task1 = semaphore.WaitAsync();
        var task2 = semaphore.WaitAsync();
        var task3 = semaphore.WaitAsync();

        // Assert
        Assert.That(task1.IsCompleted, Is.True);
        Assert.That(task2.IsCompleted, Is.True);
        Assert.That(task3.IsCompleted, Is.True);

        // Fourth wait should block
        var task4 = semaphore.WaitAsync();
        await Task.Delay(50);
        Assert.That(task4.IsCompleted, Is.False, "Fourth wait should block after count exhausted");
    }

    [Test]
    public async Task Release_WithNoWaiters_IncrementsCount()
    {
        // Arrange
        using var semaphore = new AsyncSemaphore(0);

        // Act
        semaphore.Release();

        // Assert - Next wait should succeed immediately
        var task = semaphore.WaitAsync();
        Assert.That(task.IsCompleted, Is.True, "Count should have been incremented by Release()");
        await task;
    }

    [Test]
    public async Task Release_WithQueuedWaiter_CompletesWaiter()
    {
        // Arrange
        using var semaphore = new AsyncSemaphore(0);
        var waitTask = semaphore.WaitAsync();

        // Act
        semaphore.Release();

        // Assert
        await waitTask.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.That(waitTask.IsCompleted, Is.True, "Waiter should be completed by Release()");
    }

    [Test]
    public async Task Release_WithMultipleWaiters_CompletesFIFO()
    {
        // Arrange
        using var semaphore = new AsyncSemaphore(0);
        var completionOrder = new List<int>();

        var task1 = Task.Run(async () =>
        {
            await semaphore.WaitAsync();
            completionOrder.Add(1);
        });

        await Task.Delay(100);
        var task2 = Task.Run(async () =>
        {
            await semaphore.WaitAsync();
            completionOrder.Add(2);
        });

        await Task.Delay(100);
        var task3 = Task.Run(async () =>
        {
            await semaphore.WaitAsync();
            completionOrder.Add(3);
        });

        // Wait for all tasks to be queued
        await Task.Delay(100);

        // Act - Release in order
        semaphore.Release();
        await Task.Delay(50);
        semaphore.Release();
        await Task.Delay(50);
        semaphore.Release();

        // Assert
        await Task.WhenAll(task1, task2, task3).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(completionOrder, Is.EqualTo(new[] { 1, 2, 3 }), "Waiters should complete in FIFO order");
    }

    [Test]
    public async Task WaitAsync_WithCancellationToken_CancelsWait()
    {
        // Arrange
        using var semaphore = new AsyncSemaphore(0);
        using var cts = new CancellationTokenSource();

        // Act
        var waitTask = semaphore.WaitAsync(cts.Token);
        await Task.Delay(50);
        cts.Cancel();

        // Assert
        Assert.ThrowsAsync<TaskCanceledException>(async () => await waitTask);
    }

    [Test]
    public async Task WaitAsync_WithAlreadyCanceledToken_ThrowsImmediately()
    {
        // Arrange
        using var semaphore = new AsyncSemaphore(0);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        Assert.ThrowsAsync<TaskCanceledException>(async () => await semaphore.WaitAsync(cts.Token));
    }

    [Test]
    public async Task Release_SkipsCanceledWaiters()
    {
        // Arrange
        using var semaphore = new AsyncSemaphore(0);
        using var cts1 = new CancellationTokenSource();
        using var cts2 = new CancellationTokenSource();

        var wait1 = semaphore.WaitAsync(cts1.Token);
        var wait2 = semaphore.WaitAsync(cts2.Token);
        var wait3 = semaphore.WaitAsync();

        await Task.Delay(50);

        // Cancel first two waiters
        cts1.Cancel();
        cts2.Cancel();
        await Task.Delay(50);

        // Act - Release should skip canceled waiters and complete wait3
        semaphore.Release();

        // Assert
        await wait3.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.That(wait3.IsCompleted, Is.True, "Release should skip canceled waiters");
        Assert.That(wait1.IsCanceled, Is.True);
        Assert.That(wait2.IsCanceled, Is.True);
    }

    [Test]
    public void Dispose_CompletesSuccessfully()
    {
        // Arrange
        var semaphore = new AsyncSemaphore(1);

        // Act & Assert
        Assert.DoesNotThrow(() => semaphore.Dispose());
    }

    [Test]
    public void Dispose_MultipleTimes_DoesNotThrow()
    {
        // Arrange
        var semaphore = new AsyncSemaphore(1);

        // Act & Assert
        Assert.DoesNotThrow(() =>
        {
            semaphore.Dispose();
            semaphore.Dispose();
            semaphore.Dispose();
        });
    }

    [Test]
    public async Task Dispose_WithPendingWaiters_ThrowsObjectDisposedException()
    {
        // Arrange
        var semaphore = new AsyncSemaphore(0);
        var waitTask = semaphore.WaitAsync();

        // Act
        semaphore.Dispose();

        // Assert
        var ex = Assert.ThrowsAsync<ObjectDisposedException>(async () => await waitTask);
        Assert.That(ex!.ObjectName, Is.EqualTo("AsyncSemaphore"));
    }

    [Test]
    public void WaitAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        // Arrange
        var semaphore = new AsyncSemaphore(1);
        semaphore.Dispose();

        // Act & Assert
        var ex = Assert.Throws<ObjectDisposedException>(() => semaphore.WaitAsync());
        Assert.That(ex!.ObjectName, Is.EqualTo("AsyncSemaphore"));
    }

    [Test]
    public void Release_AfterDispose_IsANoOp()
    {
        // Arrange
        var semaphore = new AsyncSemaphore(1);
        semaphore.Dispose();

        // Act & Assert - Cleanup paths may release after disposal; this must not throw
        // so callbacks that follow a Release are never skipped.
        Assert.DoesNotThrow(() => semaphore.Release());
    }

    [Test]
    public async Task ConcurrentWaitAndRelease_HandlesCorrectly()
    {
        // Arrange
        using var semaphore = new AsyncSemaphore(5);
        var tasks = new List<Task>();
        var completedCount = 0;

        // Act - Create many concurrent wait/release operations
        for (int i = 0; i < 20; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync();
                Interlocked.Increment(ref completedCount);
                await Task.Delay(10);
                semaphore.Release();
            }));
        }

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.That(completedCount, Is.EqualTo(20), "All operations should complete");
    }

    [Test]
    public async Task ConcurrentOperations_MaintainCorrectCount()
    {
        // Arrange
        using var semaphore = new AsyncSemaphore(3);
        var maxConcurrent = 0;
        var currentConcurrent = 0;
        var lockObj = new object();

        var tasks = Enumerable.Range(0, 10).Select(async i =>
        {
            await semaphore.WaitAsync();
            try
            {
                lock (lockObj)
                {
                    currentConcurrent++;
                    maxConcurrent = Math.Max(maxConcurrent, currentConcurrent);
                }

                await Task.Delay(50);

                lock (lockObj)
                {
                    currentConcurrent--;
                }
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        // Act
        await Task.WhenAll(tasks);

        // Assert
        Assert.That(maxConcurrent, Is.LessThanOrEqualTo(3), "Should never exceed initial count");
        Assert.That(maxConcurrent, Is.EqualTo(3), "Should reach maximum concurrency");
    }

    [Test]
    public async Task WaitAsync_CancellationDoesNotAffectOtherWaiters()
    {
        // Arrange
        using var semaphore = new AsyncSemaphore(0);
        using var cts = new CancellationTokenSource();

        var cancelableWait = semaphore.WaitAsync(cts.Token);
        var normalWait = semaphore.WaitAsync();

        await Task.Delay(50);

        // Act - Cancel first waiter
        cts.Cancel();
        await Task.Delay(50);

        // Release should complete the second waiter
        semaphore.Release();

        // Assert
        await normalWait.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.That(normalWait.IsCompleted, Is.True, "Normal wait should complete");
        Assert.That(cancelableWait.IsCanceled, Is.True, "Canceled wait should be canceled");
    }

    [Test]
    public async Task Release_MultipleReleases_BuildsUpCount()
    {
        // Arrange
        using var semaphore = new AsyncSemaphore(0);

        // Act - Release multiple times with no waiters
        semaphore.Release();
        semaphore.Release();
        semaphore.Release();

        // Assert - Should be able to wait 3 times immediately
        var task1 = semaphore.WaitAsync();
        var task2 = semaphore.WaitAsync();
        var task3 = semaphore.WaitAsync();

        Assert.That(task1.IsCompleted, Is.True);
        Assert.That(task2.IsCompleted, Is.True);
        Assert.That(task3.IsCompleted, Is.True);

        await Task.WhenAll(task1, task2, task3);
    }

    [Test]
    public async Task Dispose_CancelsAllPendingWaiters()
    {
        // Arrange
        var semaphore = new AsyncSemaphore(0);
        var wait1 = semaphore.WaitAsync();
        var wait2 = semaphore.WaitAsync();
        var wait3 = semaphore.WaitAsync();

        await Task.Delay(50);

        // Act
        semaphore.Dispose();

        // Assert
        Assert.ThrowsAsync<ObjectDisposedException>(async () => await wait1);
        Assert.ThrowsAsync<ObjectDisposedException>(async () => await wait2);
        Assert.ThrowsAsync<ObjectDisposedException>(async () => await wait3);
    }

    [Test]
    public async Task StressTest_ManyOperations()
    {
        // Arrange
        using var semaphore = new AsyncSemaphore(10);
        var operationCount = 100;
        var successCount = 0;

        // Act
        var tasks = Enumerable.Range(0, operationCount).Select(async _ =>
        {
            await semaphore.WaitAsync();
            try
            {
                await Task.Delay(1);
                Interlocked.Increment(ref successCount);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        Assert.That(successCount, Is.EqualTo(operationCount), "All operations should complete successfully");
    }
}
