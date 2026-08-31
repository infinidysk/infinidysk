using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Api.Controllers;
using NzbWebDAV.Api.Controllers.UsenetMigration;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Database.Models.UsenetMigration;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Services.StreamTrace;
using NzbWebDAV.Tests.Database;
using NzbWebDAV.UsenetMigration;
using NzbWebDAV.UsenetMigration.Runner;
using NzbWebDAV.UsenetMigration.Symlinks;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Tests.UsenetMigration;

[Collection(nameof(ConfigPathCollection))]
public sealed class Step6LifecycleTests
{
    private sealed class TestSymlinkOps(string path, string currentTarget) : ISymlinkOps
    {
        public string? ReadLink(string libraryRoot, string candidatePath) =>
            candidatePath == path ? currentTarget : null;

        public void ReplaceSymlink(string libraryRoot, string candidatePath, string expectedOldTarget, string target)
        {
            currentTarget = target;
        }

        public void DeleteSymlink(string libraryRoot, string candidatePath, string expectedTarget)
        {
            currentTarget = null!;
        }

        public void CreateSymlink(string libraryRoot, string candidatePath, string target)
        {
            currentTarget = target;
        }
    }

    [Fact]
    public async Task Restore_BlocksPlanAndApply_AndDrainsAfterClientDisconnect()
    {
        await using var h = await MigrationTestHarness.CreateAsync();
        var root = Directory.CreateTempSubdirectory("altmig-library-");
        var backups = Directory.CreateTempSubdirectory("altmig-backups-");
        var link = Path.Join(root.FullName, "movie.mkv");
        var original = "/mnt/altmount/movie.mkv";
        var replacement = "/mnt/nzbdav/.ids/x";
        var archiveName = "altmount-symlink-backup-20260721-120000.tar.gz";
        await SymlinkBackup.WriteAsync(
            Path.Join(backups.FullName, archiveName),
            [new SymlinkBackup.Entry(link, original, replacement)]);
        await h.Store.UpdateSessionAsync(s =>
        {
            s.Status = "linked";
            s.SymlinkLibraryRoot = root.FullName;
            s.SymlinkBackupDir = backups.FullName;
        });

        using var queueManager = CreateQueueManager();
        var runner = new UsenetMigrationRunner(
            h.Store, queueManager, new ConfigManager(), new WebsocketManager());
        var ops = new TestSymlinkOps(link, replacement);
        runner.SymlinkRestoreServiceForTests.Ops = ops;
        var filesystemWorkEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFilesystemWork = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        runner.SymlinkRestoreServiceForTests.BeforeFilesystemWorkForTests = async ct =>
        {
            filesystemWorkEntered.TrySetResult();
            await releaseFilesystemWork.Task.WaitAsync(ct);
        };
        using var disconnectedClient = new CancellationTokenSource();

        try
        {
            var restore = runner.RestoreSymlinksAsync(archiveName, disconnectedClient.Token);
            await filesystemWorkEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal("restoring", (await h.Store.GetSessionAsync()).Status);
            var plan = await h.Store.StartSymlinkPlanAsync(root.FullName, backups.FullName);
            var apply = await h.Store.TryTransitionSessionAsync(MigrationSessionTransition.StartApply);
            Assert.Equal(MigrationSessionTransitionOutcome.Rejected, plan.Outcome);
            Assert.Equal(MigrationSessionTransitionOutcome.Rejected, apply.Outcome);

            disconnectedClient.Cancel();
            releaseFilesystemWork.TrySetResult();

            var summary = await restore.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, summary.Restored);
            Assert.Equal("linked", (await h.Store.GetSessionAsync()).Status);
        }
        finally
        {
            releaseFilesystemWork.TrySetResult();
            root.Delete(recursive: true);
            backups.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PlanFailure_ReturnsToLinkedInsteadOfTrappingWizard()
    {
        await using var h = await MigrationTestHarness.CreateAsync();
        await h.Store.UpdateSessionAsync(s =>
        {
            s.Status = "linking";
            s.SymlinkLibraryRoot = "/library";
        });
        using var queueManager = CreateQueueManager();
        var runner = new UsenetMigrationRunner(
            h.Store, queueManager, new ConfigManager(), new WebsocketManager());
        runner.SymlinkPlannerForTests.LibraryRootValidator = _ =>
            throw new IOException("simulated plan failure");

        await runner.TickOnceForTestsAsync();

        Assert.Equal("linked", (await h.Store.GetSessionAsync()).Status);
    }

    [Fact]
    public async Task ApplyFailure_ReturnsToLinkedInsteadOfTrappingWizard()
    {
        await using var h = await MigrationTestHarness.CreateAsync();
        var notDirectory = Path.GetTempFileName();
        try
        {
            await h.Store.UpdateSessionAsync(s =>
            {
                s.Status = "applying";
                s.SymlinkLibraryRoot = "/library";
                s.SymlinkBackupDir = notDirectory;
            });
            await using (var migration = h.Mig())
            {
                migration.SymlinkRewrites.Add(new MigrationSymlinkRewrite
                {
                    SymlinkPath = "/library/movie.mkv",
                    OldTarget = "/mnt/altmount/movie.mkv",
                    NewTarget = "/mnt/nzbdav/.ids/x",
                    Status = "rewrite",
                    UpdatedAt = DateTime.UtcNow,
                });
                await migration.SaveChangesAsync();
            }
            using var queueManager = CreateQueueManager();
            var runner = new UsenetMigrationRunner(
                h.Store, queueManager, new ConfigManager(), new WebsocketManager());

            await runner.TickOnceForTestsAsync();

            Assert.Equal("linked", (await h.Store.GetSessionAsync()).Status);
        }
        finally
        {
            File.Delete(notDirectory);
        }
    }

    [Fact]
    public async Task Apply_RequiresExplicitAcknowledgementWhileUnreadableRowsExist()
    {
        await using var h = await MigrationTestHarness.CreateAsync();
        await h.Store.UpdateSessionAsync(s =>
        {
            s.Status = "linked";
            s.SymlinkLibraryRoot = "/library";
            s.SymlinkBackupDir = "/backups";
        });
        await using (var migration = h.Mig())
        {
            migration.SymlinkRewrites.AddRange(
                new MigrationSymlinkRewrite
                {
                    SymlinkPath = "/library/rewrite.mkv",
                    OldTarget = "/mnt/altmount/rewrite.mkv",
                    NewTarget = "/mnt/nzbdav/.ids/rewrite",
                    Status = "rewrite",
                    UpdatedAt = DateTime.UtcNow,
                },
                new MigrationSymlinkRewrite
                {
                    SymlinkPath = "/library/unreadable.mkv",
                    OldTarget = "",
                    Status = "unreadable",
                    Error = "Permission denied",
                    UpdatedAt = DateTime.UtcNow,
                });
            await migration.SaveChangesAsync();
        }

        const string apiKey = "step6-unreadable-test-key";
        var previousApiKey = Environment.GetEnvironmentVariable("FRONTEND_BACKEND_API_KEY");
        Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", apiKey);
        try
        {
            var config = new ConfigManager();
            using var queueManager = CreateQueueManager();
            var runner = new UsenetMigrationRunner(
                h.Store, queueManager, config, new WebsocketManager());
            using var services = new ServiceCollection()
                .AddSingleton(config)
                .AddSingleton(h.Store)
                .BuildServiceProvider();
            var httpContext = new DefaultHttpContext { RequestServices = services };
            httpContext.Request.Headers["x-api-key"] = apiKey;
            var controller = new UsenetMigrationController(h.Store, runner)
            {
                ControllerContext = new ControllerContext { HttpContext = httpContext },
            };

            var rejected = Assert.IsType<BadRequestObjectResult>(
                await controller.ApplySymlinks(new SymlinkApplyRequest(true, null)));
            var rejection = Assert.IsType<BaseApiResponse>(rejected.Value);
            Assert.Contains("1 unreadable symlink", rejection.Error!);
            Assert.Equal("linked", (await h.Store.GetSessionAsync()).Status);

            Assert.IsType<OkObjectResult>(
                await controller.ApplySymlinks(new SymlinkApplyRequest(true, true)));
            Assert.Equal("applying", (await h.Store.GetSessionAsync()).Status);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", previousApiKey);
        }
    }

    [Fact]
    public async Task StaleRestoringState_ReturnsToLinkedOnRunnerTick()
    {
        await using var h = await MigrationTestHarness.CreateAsync();
        await h.Store.UpdateSessionAsync(s => s.Status = "restoring");
        using var queueManager = CreateQueueManager();
        var runner = new UsenetMigrationRunner(
            h.Store, queueManager, new ConfigManager(), new WebsocketManager());

        await runner.TickOnceForTestsAsync();

        Assert.Equal("linked", (await h.Store.GetSessionAsync()).Status);
    }

    [Fact]
    public async Task CancelLinkPlan_ReturnsToLinked_AndSubsequentPlanWorks()
    {
        await using var h = await MigrationTestHarness.CreateAsync();
        await h.Store.UpdateSessionAsync(s => s.Status = "linking");
        using var queueManager = CreateQueueManager();
        var runner = new UsenetMigrationRunner(
            h.Store, queueManager, new ConfigManager(), new WebsocketManager());

        const string apiKey = "step6-cancel-test-key";
        var previousApiKey = Environment.GetEnvironmentVariable("FRONTEND_BACKEND_API_KEY");
        var previousConfig = Environment.GetEnvironmentVariable("CONFIG_PATH");
        var configDir = Directory.CreateTempSubdirectory("altmig-config-");
        Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", apiKey);
        Environment.SetEnvironmentVariable("CONFIG_PATH", configDir.FullName);
        try
        {
            var config = new ConfigManager();
            using var services = new ServiceCollection()
                .AddSingleton(config)
                .AddSingleton(h.Store)
                .BuildServiceProvider();
            var httpContext = new DefaultHttpContext { RequestServices = services };
            httpContext.Request.Headers["x-api-key"] = apiKey;
            var controller = new UsenetMigrationController(h.Store, runner)
            {
                ControllerContext = new ControllerContext { HttpContext = httpContext },
            };

            Assert.IsType<OkObjectResult>(await controller.CancelSymlinkOperation());
            Assert.Equal("linked", (await h.Store.GetSessionAsync()).Status);

            var library = Directory.CreateTempSubdirectory("altmig-lib-");
            var backups = Directory.CreateTempSubdirectory("altmig-bak-");
            try
            {
                await h.Store.UpdateSessionAsync(s => s.Status = "complete");
                Assert.IsType<OkObjectResult>(await controller.PlanSymlinks(
                    new SymlinkPlanRequest(library.FullName, backups.FullName)));
                Assert.Equal("linking", (await h.Store.GetSessionAsync()).Status);

                await h.Store.UpdateSessionAsync(s => s.Status = "complete");
                Assert.IsType<OkObjectResult>(await controller.PlanSymlinks(
                    new SymlinkPlanRequest(library.FullName, null)));
                var session = await h.Store.GetSessionAsync();
                Assert.Equal("linking", session.Status);
                Assert.Equal(
                    Path.Join(configDir.FullName, "migration-backups"),
                    session.SymlinkBackupDir);
            }
            finally
            {
                library.Delete(true);
                backups.Delete(true);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", previousApiKey);
            Environment.SetEnvironmentVariable("CONFIG_PATH", previousConfig);
            configDir.Delete(true);
        }
    }

    [Fact]
    public async Task PlanSymlinks_RejectsFilesystemRootAndConfigOverlap()
    {
        await using var h = await MigrationTestHarness.CreateAsync();
        await h.Store.UpdateSessionAsync(s => s.Status = "complete");
        using var queueManager = CreateQueueManager();
        var runner = new UsenetMigrationRunner(
            h.Store, queueManager, new ConfigManager(), new WebsocketManager());

        const string apiKey = "step6-root-guard-key";
        var previousApiKey = Environment.GetEnvironmentVariable("FRONTEND_BACKEND_API_KEY");
        var previousConfig = Environment.GetEnvironmentVariable("CONFIG_PATH");
        var configDir = Directory.CreateTempSubdirectory("altmig-config-");
        Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", apiKey);
        Environment.SetEnvironmentVariable("CONFIG_PATH", configDir.FullName);
        try
        {
            var config = new ConfigManager();
            using var services = new ServiceCollection()
                .AddSingleton(config)
                .AddSingleton(h.Store)
                .BuildServiceProvider();
            var httpContext = new DefaultHttpContext { RequestServices = services };
            httpContext.Request.Headers["x-api-key"] = apiKey;
            var controller = new UsenetMigrationController(h.Store, runner)
            {
                ControllerContext = new ControllerContext { HttpContext = httpContext },
            };

            var backups = Directory.CreateTempSubdirectory("altmig-bak-");
            try
            {
                var rootRejected = Assert.IsType<BadRequestObjectResult>(
                    await controller.PlanSymlinks(new SymlinkPlanRequest("/", backups.FullName)));
                Assert.Contains("filesystem root", Assert.IsType<BaseApiResponse>(rootRejected.Value).Error!);

                var configRejected = Assert.IsType<BadRequestObjectResult>(
                    await controller.PlanSymlinks(new SymlinkPlanRequest(configDir.FullName, backups.FullName)));
                Assert.Contains("config directory", Assert.IsType<BaseApiResponse>(configRejected.Value).Error!);

                var nestedBackup = Path.Join(configDir.FullName, "nested-backup");
                Directory.CreateDirectory(nestedBackup);
                // Use a valid library that is not config, but put backup inside it.
                var library = Directory.CreateTempSubdirectory("altmig-lib-");
                var inside = Path.Join(library.FullName, "backups");
                var insideRejected = Assert.IsType<BadRequestObjectResult>(
                    await controller.PlanSymlinks(new SymlinkPlanRequest(library.FullName, inside)));
                Assert.Contains("inside libraryRoot", Assert.IsType<BaseApiResponse>(insideRejected.Value).Error!);
                library.Delete(true);
            }
            finally
            {
                backups.Delete(true);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", previousApiKey);
            Environment.SetEnvironmentVariable("CONFIG_PATH", previousConfig);
            configDir.Delete(true);
        }
    }

    [Fact]
    public async Task RemoveOrphans_RequiresConfirmationAndClaimsBackgroundOperation()
    {
        await using var h = await MigrationTestHarness.CreateAsync();
        var library = Directory.CreateTempSubdirectory("altmig-library-");
        var backups = Directory.CreateTempSubdirectory("altmig-backups-");
        await h.Store.UpdateSessionAsync(s =>
        {
            s.Status = "linked";
            s.SymlinkLibraryRoot = library.FullName;
            s.SymlinkBackupDir = backups.FullName;
        });
        await using (var migration = h.Mig())
        {
            migration.SymlinkRewrites.Add(new MigrationSymlinkRewrite
            {
                SymlinkPath = Path.Join(library.FullName, "orphan.mkv"),
                OldTarget = "/mnt/altmount/orphan.mkv",
                Status = "orphan",
                UpdatedAt = DateTime.UtcNow,
            });
            await migration.SaveChangesAsync();
        }

        const string apiKey = "step6-orphan-removal-key";
        var previousApiKey = Environment.GetEnvironmentVariable("FRONTEND_BACKEND_API_KEY");
        Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", apiKey);
        try
        {
            var config = new ConfigManager();
            using var queueManager = CreateQueueManager();
            var runner = new UsenetMigrationRunner(
                h.Store, queueManager, config, new WebsocketManager());
            using var services = new ServiceCollection()
                .AddSingleton(config)
                .AddSingleton(h.Store)
                .BuildServiceProvider();
            var httpContext = new DefaultHttpContext { RequestServices = services };
            httpContext.Request.Headers["x-api-key"] = apiKey;
            var controller = new UsenetMigrationController(h.Store, runner)
            {
                ControllerContext = new ControllerContext { HttpContext = httpContext },
            };

            var rejected = Assert.IsType<BadRequestObjectResult>(
                await controller.RemoveOrphanSymlinks(new SymlinkOrphanRemovalRequest(null)));
            Assert.Contains("explicit confirmation", Assert.IsType<BaseApiResponse>(rejected.Value).Error!);
            Assert.Equal("linked", (await h.Store.GetSessionAsync()).Status);

            Assert.IsType<OkObjectResult>(
                await controller.RemoveOrphanSymlinks(new SymlinkOrphanRemovalRequest(true)));
            Assert.Equal("removing_orphans", (await h.Store.GetSessionAsync()).Status);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", previousApiKey);
            library.Delete(recursive: true);
            backups.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Runner_OrphanRemovalCompletesAndReturnsToLinked()
    {
        await using var h = await MigrationTestHarness.CreateAsync();
        var library = Directory.CreateTempSubdirectory("altmig-library-");
        var backups = Directory.CreateTempSubdirectory("altmig-backups-");
        var link = Path.Join(library.FullName, "orphan.mkv");
        const string target = "/mnt/altmount/orphan.mkv";
        await h.Store.UpdateSessionAsync(s =>
        {
            s.Status = "removing_orphans";
            s.SymlinkLibraryRoot = library.FullName;
            s.SymlinkBackupDir = backups.FullName;
        });
        await using (var migration = h.Mig())
        {
            migration.SymlinkRewrites.Add(new MigrationSymlinkRewrite
            {
                SymlinkPath = link,
                OldTarget = target,
                Status = "orphan",
                UpdatedAt = DateTime.UtcNow,
            });
            await migration.SaveChangesAsync();
        }

        try
        {
            using var queueManager = CreateQueueManager();
            var runner = new UsenetMigrationRunner(
                h.Store, queueManager, new ConfigManager(), new WebsocketManager());
            runner.SymlinkOrphanRemoverForTests.Ops = new TestSymlinkOps(link, target);

            await runner.TickOnceForTestsAsync();

            Assert.Equal("linked", (await h.Store.GetSessionAsync()).Status);
            await using var verify = h.Mig();
            Assert.Equal("removed", (await verify.SymlinkRewrites.SingleAsync()).Status);
            Assert.Single(Directory.EnumerateFiles(
                backups.FullName, "altmount-orphan-symlink-backup-*.tar.gz"));
        }
        finally
        {
            library.Delete(recursive: true);
            backups.Delete(recursive: true);
        }
    }

    private static QueueManager CreateQueueManager()
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetProviders,
                ConfigValue = JsonSerializer.Serialize(new UsenetProviderConfig()),
            },
        ]);
        var websocket = new WebsocketManager();
        var usenet = new UsenetStreamingClient(
            config,
            websocket,
            new ProviderUsageTracker(),
            new MetricsWriter(),
            new ProviderBytesTracker(),
            new StreamTraceBuffer(100),
            new ActiveReadRegistry());
        return QueueManager.CreateForTests(
            usenet,
            config,
            websocket,
            new ProviderUsageTracker(),
            new WatchdogLog(),
            new QueueItemSourceTracker(),
            new BenchmarkGate());
    }
}
