using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Services.NativeCache;
using NzbWebDAV.Services.Prefetch;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Tests.Database;

namespace NzbWebDAV.Tests.Services;

[Collection(nameof(ConfigPathCollection))]
public sealed class PrefetchRuntimeFaultTests
{
    [Fact]
    public async Task MetadataClaimFailure_LeavesHostedRuntimeAlive_UnhealthyAndCancellable()
    {
        var root = Directory.CreateTempSubdirectory("prefetch-runtime-fault-").FullName;
        var previous = Environment.GetEnvironmentVariable("CONFIG_PATH");
        try
        {
            Environment.SetEnvironmentVariable("CONFIG_PATH", root);
            var media = Path.Combine(root, "media");
            Directory.CreateDirectory(media);
            var config = new ConfigManager();
            config.UpdateValues([
                new ConfigItem { ConfigName = ConfigKeys.CacheMode, ConfigValue = "native" },
                new ConfigItem { ConfigName = ConfigKeys.NativeCacheFolders, ConfigValue = JsonSerializer.Serialize(new[]
                    { new NativeCacheFolder { Id = "disk", Path = media, MinFreeBytes = 0 } }) }
            ]);
            using var blobs = new FileBlobStore();
            using var repairs = new RepairPatchStore(Path.Combine(root, "patches"), 100);
            await using var native = new NativeCacheService(config, blobs, repairs);
            using var services = new ServiceCollection().BuildServiceProvider();
            using var runtime = new PrefetchRuntime(config, native, services.GetRequiredService<IServiceScopeFactory>(), new ActiveReadRegistry());
            await runtime.WaitForInitializationAsync(CancellationToken.None);
            runtime.Jobs!.Enqueue(Guid.NewGuid(), "manual", 0);
            using (var database = new SqliteConnection("Data Source=" + Path.Combine(native.ActiveSettings!.MetadataPath, "prefetch.db")))
            {
                database.Open();
                using var command = database.CreateCommand();
                command.CommandText = "DROP TABLE Jobs";
                command.ExecuteNonQuery();
            }
            await runtime.StartAsync(CancellationToken.None);
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                while (runtime.RuntimeError is null && !runtime.ExecuteTask!.IsCompleted)
                    await Task.Delay(10, deadline.Token);
                Assert.NotNull(runtime.RuntimeError);
                Assert.False(runtime.Healthy);
                Assert.DoesNotContain(root, runtime.RuntimeError);
                Assert.False(runtime.ExecuteTask!.IsCompleted);
            }
            finally
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await runtime.StopAsync(stop.Token);
            }
            Assert.True(runtime.ExecuteTask!.IsCompletedSuccessfully);
        }
        finally { Environment.SetEnvironmentVariable("CONFIG_PATH", previous); Directory.Delete(root, recursive: true); }
    }
}
