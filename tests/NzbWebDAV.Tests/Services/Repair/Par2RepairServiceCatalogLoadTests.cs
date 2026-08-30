using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Tests.Database;
using NzbWebDAV.Tests.TestUtils;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace NzbWebDAV.Tests.Services.Repair;

public sealed class Par2RepairServiceCatalogLoadTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task KnownFailureThenSuccess_StartsWorkersOnce()
    {
        var dir = NewTempDir("known-then-success");
        var attempts = 0;
        var workerStarts = 0;
        var delays = new List<TimeSpan>();
        var delayEntered = NewTcs();
        var allowRetry = NewTcs();
        var workersStarted = NewTcs();

        try
        {
            var store = new RepairPatchStore(dir, 1024 * 1024, _ =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                    throw new IOException("catalog temporarily unavailable");
                return [];
            });
            var service = CreateService(store, Delay, () =>
            {
                Interlocked.Increment(ref workerStarts);
                workersStarted.TrySetResult();
            });

            await service.StartAsync(CancellationToken.None);
            try
            {
                await delayEntered.Task.WaitAsync(Timeout);
                Assert.False(store.IsCatalogReady);
                Assert.Equal(1, Volatile.Read(ref attempts));
                Assert.Equal(0, Volatile.Read(ref workerStarts));

                allowRetry.TrySetResult();
                await workersStarted.Task.WaitAsync(Timeout);

                Assert.True(store.IsCatalogReady);
                Assert.Equal(2, Volatile.Read(ref attempts));
                Assert.Equal(1, Volatile.Read(ref workerStarts));
                Assert.Equal(TimeSpan.FromSeconds(1), Assert.Single(delays));
            }
            finally
            {
                allowRetry.TrySetResult();
                await StopAsync(service);
            }
        }
        finally
        {
            DeleteDir(dir);
        }

        Task Delay(TimeSpan delay, CancellationToken ct)
        {
            delays.Add(delay);
            delayEntered.TrySetResult();
            return allowRetry.Task.WaitAsync(ct);
        }
    }

    [Fact]
    public async Task RepeatedKnownFailures_UseExponentialBackoff_AndStartWorkersOnce()
    {
        var dir = NewTempDir("backoff");
        var attempts = 0;
        var workerStarts = 0;
        var delays = new List<TimeSpan>();
        var workersStarted = NewTcs();

        try
        {
            var store = new RepairPatchStore(dir, 1024 * 1024, _ =>
            {
                if (Interlocked.Increment(ref attempts) <= 2)
                    throw new IOException("catalog temporarily unavailable");
                return [];
            });
            var service = CreateService(store, ImmediateDelay, () =>
            {
                Interlocked.Increment(ref workerStarts);
                workersStarted.TrySetResult();
            });

            await service.StartAsync(CancellationToken.None);
            try
            {
                await workersStarted.Task.WaitAsync(Timeout);
                Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }, delays);
                Assert.True(store.IsCatalogReady);
                Assert.Equal(3, Volatile.Read(ref attempts));
                Assert.Equal(1, Volatile.Read(ref workerStarts));
            }
            finally
            {
                await StopAsync(service);
            }
        }
        finally
        {
            DeleteDir(dir);
        }

        Task ImmediateDelay(TimeSpan delay, CancellationToken ct)
        {
            delays.Add(delay);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task UnauthorizedAccess_IsRetriedLikeKnownIo()
    {
        await AssertRetriedThenSucceeds(
            "unauthorized",
            () => new UnauthorizedAccessException("catalog access denied"));
    }

    [Fact]
    public async Task SqliteBusy_IsRetriedLikeKnownIo()
    {
        await AssertRetriedThenSucceeds(
            "sqlite-busy",
            () => new SqliteException("SQLite Error 5: 'database is locked'.", 5));
    }

    [Fact]
    public async Task SqliteCorruption_UsesCorruptionDelay()
    {
        var dir = NewTempDir("sqlite-corrupt");
        var attempts = 0;
        var workerStarts = 0;
        var delays = new List<TimeSpan>();
        var workersStarted = NewTcs();

        try
        {
            var store = new RepairPatchStore(dir, 1024 * 1024, _ =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                    throw new SqliteException("SQLite Error 11: 'database disk image is malformed'.", 11);
                return [];
            });
            var service = CreateService(store, ImmediateDelay, () =>
            {
                Interlocked.Increment(ref workerStarts);
                workersStarted.TrySetResult();
            });

            await service.StartAsync(CancellationToken.None);
            try
            {
                await workersStarted.Task.WaitAsync(Timeout);
                Assert.Equal(BackgroundServiceErrorHandler.CorruptionDelay, Assert.Single(delays));
                Assert.Equal(1, Volatile.Read(ref workerStarts));
            }
            finally
            {
                await StopAsync(service);
            }
        }
        finally
        {
            DeleteDir(dir);
        }

        Task ImmediateDelay(TimeSpan delay, CancellationToken ct)
        {
            delays.Add(delay);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task UnexpectedFailure_DoesNotRetryOrStartWorkers()
    {
        var dir = NewTempDir("unexpected");
        var failure = new InvalidOperationException("catalog iterator poisoned");
        var attempts = 0;
        var workerStarts = 0;
        var delays = new List<TimeSpan>();
        var scanEntered = NewTcs();
        var allowThrow = NewTcs();

        try
        {
            var store = new RepairPatchStore(dir, 1024 * 1024, ct =>
            {
                Interlocked.Increment(ref attempts);
                scanEntered.TrySetResult();
                allowThrow.Task.Wait(ct);
                throw failure;
            });
            var service = CreateService(store, RecordDelay, () => Interlocked.Increment(ref workerStarts));
            using var host = BuildHost(service);
            var stopping = NewTcs();
            host.Services.GetRequiredService<IHostApplicationLifetime>()
                .ApplicationStopping.Register(() => stopping.TrySetResult());

            var startTask = Task.Run(() => host.StartAsync(CancellationToken.None));
            try
            {
                await scanEntered.Task.WaitAsync(Timeout);
                await startTask.WaitAsync(Timeout);
                allowThrow.TrySetResult();
                await stopping.Task.WaitAsync(Timeout);

                Assert.True(host.Services.GetRequiredService<IHostApplicationLifetime>()
                    .ApplicationStopping.IsCancellationRequested);
                Assert.True(service.ExecuteTask!.IsFaulted);
                Assert.Same(failure, service.ExecuteTask.Exception!.GetBaseException());
                Assert.Empty(delays);
                Assert.Equal(0, Volatile.Read(ref workerStarts));
                Assert.False(store.IsCatalogReady);
            }
            finally
            {
                allowThrow.TrySetResult();
                await host.StopAsync(CancellationToken.None).WaitAsync(Timeout);
            }
        }
        finally
        {
            allowThrow.TrySetResult();
            DeleteDir(dir);
        }

        Task RecordDelay(TimeSpan delay, CancellationToken ct)
        {
            delays.Add(delay);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ShutdownDuringBlockedScan_DoesNotRetryOrStartWorkers()
    {
        var dir = NewTempDir("shutdown-scan");
        var attempts = 0;
        var workerStarts = 0;
        var delays = new List<TimeSpan>();
        var scanEntered = NewTcs();
        using var releaseScan = new ManualResetEventSlim(false);

        try
        {
            var store = new RepairPatchStore(dir, 1024 * 1024, ct =>
            {
                Interlocked.Increment(ref attempts);
                scanEntered.TrySetResult();
                releaseScan.Wait(ct);
                return [];
            });
            var service = CreateService(store, RecordDelay, () => Interlocked.Increment(ref workerStarts));
            using var host = BuildHost(service);

            var startTask = Task.Run(() => host.StartAsync(CancellationToken.None));
            try
            {
                await scanEntered.Task.WaitAsync(Timeout);
                await startTask.WaitAsync(Timeout);
                await host.StopAsync(CancellationToken.None).WaitAsync(Timeout);

                Assert.Equal(1, Volatile.Read(ref attempts));
                Assert.Empty(delays);
                Assert.Equal(0, Volatile.Read(ref workerStarts));
                Assert.False(store.IsCatalogReady);
            }
            finally
            {
                releaseScan.Set();
            }
        }
        finally
        {
            releaseScan.Set();
            DeleteDir(dir);
        }

        Task RecordDelay(TimeSpan delay, CancellationToken ct)
        {
            delays.Add(delay);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ShutdownDuringRetryDelay_DoesNotStartSecondScan()
    {
        var dir = NewTempDir("shutdown-delay");
        var attempts = 0;
        var workerStarts = 0;
        var delays = new List<TimeSpan>();
        var delayEntered = NewTcs();
        var blockDelay = NewTcs();

        try
        {
            var store = new RepairPatchStore(dir, 1024 * 1024, _ =>
            {
                Interlocked.Increment(ref attempts);
                throw new IOException("catalog temporarily unavailable");
            });
            var service = CreateService(store, Delay, () => Interlocked.Increment(ref workerStarts));
            using var host = BuildHost(service);

            await host.StartAsync(CancellationToken.None).WaitAsync(Timeout);
            try
            {
                await delayEntered.Task.WaitAsync(Timeout);
                await host.StopAsync(CancellationToken.None).WaitAsync(Timeout);

                Assert.Equal(1, Volatile.Read(ref attempts));
                Assert.Equal(TimeSpan.FromSeconds(1), Assert.Single(delays));
                Assert.Equal(0, Volatile.Read(ref workerStarts));
                Assert.False(store.IsCatalogReady);
            }
            finally
            {
                blockDelay.TrySetCanceled();
            }
        }
        finally
        {
            blockDelay.TrySetCanceled();
            DeleteDir(dir);
        }

        Task Delay(TimeSpan delay, CancellationToken ct)
        {
            delays.Add(delay);
            delayEntered.TrySetResult();
            return blockDelay.Task.WaitAsync(ct);
        }
    }

    [Fact]
    public async Task NonShutdownCancellation_DoesNotRetryOrStartWorkers()
    {
        var dir = NewTempDir("non-shutdown-cancel");
        var attempts = 0;
        var workerStarts = 0;
        var delays = new List<TimeSpan>();
        var scanEntered = NewTcs();
        var allowThrow = NewTcs();

        try
        {
            var store = new RepairPatchStore(dir, 1024 * 1024, ct =>
            {
                Interlocked.Increment(ref attempts);
                scanEntered.TrySetResult();
                allowThrow.Task.Wait(ct);
                throw new OperationCanceledException();
            });
            var service = CreateService(store, RecordDelay, () => Interlocked.Increment(ref workerStarts));
            using var host = BuildHost(service);
            var stopping = NewTcs();
            host.Services.GetRequiredService<IHostApplicationLifetime>()
                .ApplicationStopping.Register(() => stopping.TrySetResult());

            var startTask = Task.Run(() => host.StartAsync(CancellationToken.None));
            try
            {
                await scanEntered.Task.WaitAsync(Timeout);
                await startTask.WaitAsync(Timeout);
                allowThrow.TrySetResult();
                await stopping.Task.WaitAsync(Timeout);

                Assert.True(service.ExecuteTask!.IsCompleted);
                Assert.False(service.ExecuteTask.IsCompletedSuccessfully);
                Assert.Empty(delays);
                Assert.Equal(0, Volatile.Read(ref workerStarts));
                Assert.False(store.IsCatalogReady);
            }
            finally
            {
                allowThrow.TrySetResult();
                await host.StopAsync(CancellationToken.None).WaitAsync(Timeout);
            }
        }
        finally
        {
            allowThrow.TrySetResult();
            DeleteDir(dir);
        }

        Task RecordDelay(TimeSpan delay, CancellationToken ct)
        {
            delays.Add(delay);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task OutOfMemory_DoesNotRetryOrStartWorkers()
    {
        await AssertFatalDoesNotRetry(new OutOfMemoryException("scripted"));
    }

    [Fact]
    public async Task NestedFatal_TakesPrecedenceOverKnownIo()
    {
        await AssertFatalDoesNotRetry(new AggregateException(
            new OutOfMemoryException("scripted"),
            new IOException("disk")));
    }

    [Fact]
    public async Task HostStartAsync_ReturnsWhileCatalogScanBlocks()
    {
        var dir = NewTempDir("host-start");
        var scanEntered = NewTcs();
        using var releaseScan = new ManualResetEventSlim(false);

        try
        {
            var store = new RepairPatchStore(dir, 1024 * 1024, ct =>
            {
                scanEntered.TrySetResult();
                releaseScan.Wait(ct);
                return [];
            });
            var service = CreateService(store, (_, _) => Task.CompletedTask);
            using var host = BuildHost(service);

            var startTask = Task.Run(() => host.StartAsync(CancellationToken.None));
            try
            {
                await scanEntered.Task.WaitAsync(Timeout);
                var startCompleted = await Task.WhenAny(startTask, Task.Delay(Timeout)) == startTask;
                Assert.True(startCompleted, "Host startup must not wait for the patch catalog scan to finish.");
            }
            finally
            {
                releaseScan.Set();
            }

            await startTask.WaitAsync(Timeout);
            await host.StopAsync(CancellationToken.None).WaitAsync(Timeout);
        }
        finally
        {
            releaseScan.Set();
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task PersistentKnownFailures_StayInRetryUntilShutdown()
    {
        var dir = NewTempDir("persistent");
        var attempts = 0;
        var workerStarts = 0;
        var delays = new List<TimeSpan>();
        var delayCount = 0;
        var fourthDelay = NewTcs();
        var blockFourth = NewTcs();

        try
        {
            var store = new RepairPatchStore(dir, 1024 * 1024, _ =>
            {
                Interlocked.Increment(ref attempts);
                throw new IOException("catalog persistently unavailable");
            });
            var service = CreateService(store, Delay, () => Interlocked.Increment(ref workerStarts));

            await service.StartAsync(CancellationToken.None);
            try
            {
                await fourthDelay.Task.WaitAsync(Timeout);
                Assert.False(store.IsCatalogReady);
                Assert.Equal(4, Volatile.Read(ref attempts));
                Assert.Equal(0, Volatile.Read(ref workerStarts));
                Assert.Equal(4, delays.Count);

                await StopAsync(service);

                Assert.Equal(4, Volatile.Read(ref attempts));
                Assert.Equal(0, Volatile.Read(ref workerStarts));
                Assert.False(store.IsCatalogReady);
            }
            finally
            {
                blockFourth.TrySetCanceled();
            }
        }
        finally
        {
            blockFourth.TrySetCanceled();
            DeleteDir(dir);
        }

        Task Delay(TimeSpan delay, CancellationToken ct)
        {
            delays.Add(delay);
            if (Interlocked.Increment(ref delayCount) < 4)
                return Task.CompletedTask;
            fourthDelay.TrySetResult();
            return blockFourth.Task.WaitAsync(ct);
        }
    }

    private static async Task AssertRetriedThenSucceeds(string prefix, Func<Exception> createFailure)
    {
        var dir = NewTempDir(prefix);
        var attempts = 0;
        var workerStarts = 0;
        var delays = new List<TimeSpan>();
        var workersStarted = NewTcs();

        try
        {
            var store = new RepairPatchStore(dir, 1024 * 1024, _ =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                    throw createFailure();
                return [];
            });
            var service = CreateService(store, ImmediateDelay, () =>
            {
                Interlocked.Increment(ref workerStarts);
                workersStarted.TrySetResult();
            });

            await service.StartAsync(CancellationToken.None);
            try
            {
                await workersStarted.Task.WaitAsync(Timeout);
                Assert.Equal(TimeSpan.FromSeconds(1), Assert.Single(delays));
                Assert.True(store.IsCatalogReady);
                Assert.Equal(1, Volatile.Read(ref workerStarts));
            }
            finally
            {
                await StopAsync(service);
            }
        }
        finally
        {
            DeleteDir(dir);
        }

        Task ImmediateDelay(TimeSpan delay, CancellationToken ct)
        {
            delays.Add(delay);
            return Task.CompletedTask;
        }
    }

    private static async Task AssertFatalDoesNotRetry(Exception failure)
    {
        var dir = NewTempDir("fatal");
        var workerStarts = 0;
        var delays = new List<TimeSpan>();
        var scanEntered = NewTcs();
        var allowThrow = NewTcs();

        try
        {
            var store = new RepairPatchStore(dir, 1024 * 1024, ct =>
            {
                scanEntered.TrySetResult();
                allowThrow.Task.Wait(ct);
                throw failure;
            });
            var service = CreateService(store, RecordDelay, () => Interlocked.Increment(ref workerStarts));

            await service.StartAsync(CancellationToken.None);
            try
            {
                await scanEntered.Task.WaitAsync(Timeout);
                allowThrow.TrySetResult();
                await Assert.ThrowsAnyAsync<Exception>(() => service.ExecuteTask!.WaitAsync(Timeout));

                Assert.True(service.ExecuteTask!.IsFaulted);
                Assert.Empty(delays);
                Assert.Equal(0, Volatile.Read(ref workerStarts));
                Assert.False(store.IsCatalogReady);
            }
            finally
            {
                allowThrow.TrySetResult();
                if (service.ExecuteTask is not { IsFaulted: true })
                    await StopAsync(service);
            }
        }
        finally
        {
            allowThrow.TrySetResult();
            DeleteDir(dir);
        }

        Task RecordDelay(TimeSpan delay, CancellationToken ct)
        {
            delays.Add(delay);
            return Task.CompletedTask;
        }
    }

    private static Par2RepairService CreateService(
        RepairPatchStore store,
        Func<TimeSpan, CancellationToken, Task> delay,
        Action? onWorkers = null,
        IDbContextFactory<DavDatabaseContext>? dbFactory = null,
        ConfigManager? config = null)
    {
        var service = new Par2RepairService(
            config ?? new ConfigManager(),
            null!,
            store,
            dbFactory ?? new ThrowingDbContextFactory(),
            delay);
        if (onWorkers != null)
            service.OnWorkersStarting = onWorkers;
        return service;
    }

    private static IHost BuildHost(Par2RepairService service) =>
        new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(service);
                services.AddHostedService(_ => service);
            })
            .Build();

    private static async Task StopAsync(Par2RepairService service)
    {
        using var cts = new CancellationTokenSource(Timeout);
        await service.StopAsync(cts.Token);
    }

    private static TaskCompletionSource NewTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string NewTempDir(string prefix) =>
        Path.Join(Path.GetTempPath(), $"nzbdav-par2-catalog-{prefix}-" + Guid.NewGuid().ToString("N"));

    private static void DeleteDir(string dir)
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    private sealed class ThrowingDbContextFactory : IDbContextFactory<DavDatabaseContext>
    {
        public DavDatabaseContext CreateDbContext() =>
            throw new InvalidOperationException("catalog-load tests do not open SQLite.");
    }
}

[Collection(nameof(ConfigPathCollection))]
public sealed class Par2RepairServiceCatalogRetentionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ZeroFillReports_StayDeduplicatedWhileCatalogRetryBlocksWorkers()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"nzbdav-catalog-zf-{Guid.NewGuid():N}");
        var patchDir = Path.Join(tempDir, "patches");
        Directory.CreateDirectory(tempDir);
        var prevConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
        var attempts = 0;
        var workerStarts = 0;
        var delayEntered = NewTcs();
        var allowRetry = NewTcs();
        var workersStarted = NewTcs();

        try
        {
            Environment.SetEnvironmentVariable("CONFIG_PATH", tempDir);
            DavDatabaseContext.ResetOptionsForTests();
            await using (var ctx = new DavDatabaseContext())
                await ctx.Database.EnsureCreatedAsync();

            var config = new ConfigManager();
            config.UpdateValues(
            [
                new ConfigItem { ConfigName = ConfigKeys.RepairEnable, ConfigValue = "true" },
            ]);
            var store = new RepairPatchStore(patchDir, 1024 * 1024, _ =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                    throw new IOException("catalog temporarily unavailable");
                return Directory.EnumerateFiles(patchDir, "*", SearchOption.AllDirectories);
            });
            var service = new Par2RepairService(config, null!, store, dbContextFactory: null, Delay);
            service.OnWorkersStarting = () =>
            {
                Interlocked.Increment(ref workerStarts);
                workersStarted.TrySetResult();
            };

            await service.StartAsync(CancellationToken.None);
            try
            {
                await delayEntered.Task.WaitAsync(Timeout);
                for (var i = 0; i < 1_000; i++)
                    service.ReportZeroFill("/view/same.mkv", $"seg{i}@test");

                Assert.Equal(1, service.PendingZeroFillCount);
                Assert.Equal(0, Volatile.Read(ref workerStarts));

                allowRetry.TrySetResult();
                await workersStarted.Task.WaitAsync(Timeout);

                var deadline = DateTime.UtcNow + Timeout;
                while (service.PendingZeroFillCount > 0 && DateTime.UtcNow < deadline)
                    await Task.Delay(25);

                Assert.Equal(0, service.PendingZeroFillCount);
                Assert.Equal(1, Volatile.Read(ref workerStarts));
            }
            finally
            {
                allowRetry.TrySetResult();
                using var stopCts = new CancellationTokenSource(Timeout);
                await service.StopAsync(stopCts.Token);
            }
        }
        finally
        {
            allowRetry.TrySetResult();
            Environment.SetEnvironmentVariable("CONFIG_PATH", prevConfigPath);
            DavDatabaseContext.ResetOptionsForTests();
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }

        Task Delay(TimeSpan delay, CancellationToken ct)
        {
            delayEntered.TrySetResult();
            return allowRetry.Task.WaitAsync(ct);
        }
    }

    private static TaskCompletionSource NewTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

[Collection(nameof(GlobalLoggerCollection))]
public sealed class Par2RepairServiceCatalogLoadLoggingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task KnownWarning_IsSingleLineAndThrottled_ThenRecoveryLogsInformation()
    {
        var dir = NewTempDir("log-throttle");
        var attempts = 0;
        var workersStarted = NewTcs();
        var sink = new CollectingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(sink)
            .CreateLogger();

        try
        {
            var store = new RepairPatchStore(dir, 1024 * 1024, _ =>
            {
                if (Interlocked.Increment(ref attempts) <= 3)
                    throw new IOException("catalog temporarily unavailable");
                return [];
            });
            var service = new Par2RepairService(
                new ConfigManager(),
                null!,
                store,
                new ThrowingDbContextFactory(),
                (_, _) => Task.CompletedTask);
            service.OnWorkersStarting = () => workersStarted.TrySetResult();

            await service.StartAsync(CancellationToken.None);
            try
            {
                await workersStarted.Task.WaitAsync(Timeout);

                var warning = Assert.Single(sink.Events, IsCatalogWarning);
                Assert.Null(warning.Exception);
                var rendered = warning.RenderMessage();
                Assert.Contains("catalog temporarily unavailable", rendered, StringComparison.Ordinal);
                Assert.Contains("attempt 1", rendered, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("   at ", rendered, StringComparison.Ordinal);

                var recovered = Assert.Single(
                    sink.Events,
                    e => e.Level == LogEventLevel.Information
                        && e.RenderMessage().Contains("PAR2 patch catalog recovered", StringComparison.Ordinal));
                Assert.Contains("3 failed load attempt", recovered.RenderMessage(), StringComparison.Ordinal);
            }
            finally
            {
                using var cts = new CancellationTokenSource(Timeout);
                await service.StopAsync(cts.Token);
            }
        }
        finally
        {
            Log.Logger = previous;
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ShutdownCancellation_DoesNotLogCatalogFailure()
    {
        var dir = NewTempDir("log-shutdown");
        var scanEntered = NewTcs();
        using var releaseScan = new ManualResetEventSlim(false);
        var sink = new CollectingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(sink)
            .CreateLogger();

        try
        {
            var store = new RepairPatchStore(dir, 1024 * 1024, ct =>
            {
                scanEntered.TrySetResult();
                releaseScan.Wait(ct);
                return [];
            });
            var service = new Par2RepairService(
                new ConfigManager(),
                null!,
                store,
                new ThrowingDbContextFactory(),
                (_, _) => Task.CompletedTask);
            using var host = new HostBuilder()
                .ConfigureServices(services =>
                {
                    services.AddSingleton(service);
                    services.AddHostedService(_ => service);
                })
                .Build();

            var startTask = Task.Run(() => host.StartAsync(CancellationToken.None));
            try
            {
                await scanEntered.Task.WaitAsync(Timeout);
                await startTask.WaitAsync(Timeout);
                await host.StopAsync(CancellationToken.None).WaitAsync(Timeout);

                Assert.DoesNotContain(sink.Events, e =>
                    e.Level >= LogEventLevel.Warning && IsCatalogMessage(e));
            }
            finally
            {
                releaseScan.Set();
            }
        }
        finally
        {
            releaseScan.Set();
            Log.Logger = previous;
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    private static bool IsCatalogWarning(LogEvent logEvent) =>
        logEvent.Level == LogEventLevel.Warning && IsCatalogMessage(logEvent);

    private static bool IsCatalogMessage(LogEvent logEvent) =>
        logEvent.RenderMessage().Contains("PAR2 patch catalog", StringComparison.Ordinal);

    private static TaskCompletionSource NewTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string NewTempDir(string prefix) =>
        Path.Join(Path.GetTempPath(), $"nzbdav-par2-catalog-{prefix}-" + Guid.NewGuid().ToString("N"));

    private sealed class CollectingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];

        public IReadOnlyList<LogEvent> Events
        {
            get
            {
                lock (_events) return _events.ToList();
            }
        }

        public void Emit(LogEvent logEvent)
        {
            lock (_events) _events.Add(logEvent);
        }
    }

    private sealed class ThrowingDbContextFactory : IDbContextFactory<DavDatabaseContext>
    {
        public DavDatabaseContext CreateDbContext() =>
            throw new InvalidOperationException("catalog-load tests do not open SQLite.");
    }
}
