using System.Text.Json;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services.NativeCache;
using NzbWebDAV.Services.Repair;
using NzbWebDAV.Tests.Database;

namespace NzbWebDAV.Tests.Services;

[Collection(nameof(ConfigPathCollection))]
public sealed class NativeCacheOperationsTests
{
    [Fact]
    public async Task Operations_AreBounded_Cancellable_AndRequireClearConfirmation()
    {
        var root = Path.Combine(Path.GetTempPath(), "native-ops-" + Guid.NewGuid().ToString("N"));
        var old = Environment.GetEnvironmentVariable("CONFIG_PATH");
        Directory.CreateDirectory(Path.Combine(root, "media"));
        try
        {
            Environment.SetEnvironmentVariable("CONFIG_PATH", root);
            var config = new ConfigManager();
            config.UpdateValues([
                new ConfigItem { ConfigName = ConfigKeys.CacheMode, ConfigValue = "native" },
                new ConfigItem { ConfigName = ConfigKeys.NativeCacheFolders, ConfigValue = JsonSerializer.Serialize(new[] { new NativeCacheFolder { Id = "disk", Path = Path.Combine(root, "media"), MinFreeBytes = 0 } }) }
            ]);
            using var blobs = new FileBlobStore();
            await using var native = new NativeCacheService(config, blobs, new RepairPatchStore(Path.Combine(root, "patches"), 100));
            using var operations = new NativeCacheOperations(native);
            Assert.Throws<ArgumentException>(() => operations.Enqueue("disk", "clear"));
            var first = operations.Enqueue("disk", "probe");
            Assert.True(operations.Cancel(first.Id));
            Assert.Equal("cancelled", operations.GetJobs().Single().State);
            for (var index = 0; index < 7; index++) operations.Enqueue("disk", "probe");
            Assert.Throws<ArgumentException>(() => operations.Enqueue("disk", "scan"));
            Assert.Throws<ArgumentException>(() => operations.Enqueue("unknown", "probe"));
            var probe = operations.GetJobs().First(job => job.State == "queued");
            var run = typeof(NativeCacheOperations).GetMethod("RunAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            await (Task)run.Invoke(operations, [probe.Id, CancellationToken.None])!;
            var completed = operations.GetJobs().Single(job => job.Id == probe.Id);
            Assert.Equal("completed", completed.State);
            Assert.NotNull(completed.Probe);
            Assert.True(completed.Probe.DurableWriteVerified);
            Assert.True(completed.Probe.Writable);
        }
        finally { Environment.SetEnvironmentVariable("CONFIG_PATH", old); Directory.Delete(root, true); }
    }
}
