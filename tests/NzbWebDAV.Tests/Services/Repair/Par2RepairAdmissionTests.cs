using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services.Repair;

namespace NzbWebDAV.Tests.Services.Repair;

/// <summary>
/// Only one PAR2 repair runs at a time. Inline callers (health-check workers and playback
/// reports) must not park on that slot indefinitely: a health-check worker that waits is a
/// worker that is not checking any other file, so one unrepairable release used to freeze
/// the whole library scan.
/// </summary>
public sealed class Par2RepairAdmissionTests
{
    [Fact]
    public async Task InlineCaller_DefersWhenAnotherRepairHoldsTheSlot()
    {
        var directory = NewTempDirectory();
        var previousTimeout = Par2RepairService.AdmissionWaitTimeout;
        Par2RepairService.AdmissionWaitTimeout = TimeSpan.FromMilliseconds(50);
        try
        {
            var store = new RepairPatchStore(directory, 1024 * 1024);
            await store.EnsureCatalogLoadedAsync(CancellationToken.None);
            var service = new Par2RepairService(NewConfig(), null!, store);

            using var heldByAnotherRepair =
                await service.HoldAdmissionForTestsAsync(CancellationToken.None);

            var outcome = await service
                .TryPar2RepairAsync(NewItem(), ["segment@test"], CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(Par2RepairOutcome.Deferred, outcome);
        }
        finally
        {
            Par2RepairService.AdmissionWaitTimeout = previousTimeout;
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task InlineCaller_StopsWaitingOnceTheSlotIsFree()
    {
        var directory = NewTempDirectory();
        var previousTimeout = Par2RepairService.AdmissionWaitTimeout;
        Par2RepairService.AdmissionWaitTimeout = TimeSpan.FromMilliseconds(50);
        try
        {
            var store = new RepairPatchStore(directory, 1024 * 1024);
            await store.EnsureCatalogLoadedAsync(CancellationToken.None);
            var service = new Par2RepairService(NewConfig(), null!, store);

            using (await service.HoldAdmissionForTestsAsync(CancellationToken.None))
            {
                Assert.Equal(
                    Par2RepairOutcome.Deferred,
                    await service
                        .TryPar2RepairAsync(NewItem(), ["segment@test"], CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(5)));
            }

            // Releasing the slot must leave the semaphore usable: a deferral is a give-up,
            // never a leaked permit.
            using var reacquired = await service.HoldAdmissionForTestsAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(reacquired);
        }
        finally
        {
            Par2RepairService.AdmissionWaitTimeout = previousTimeout;
            DeleteTempDirectory(directory);
        }
    }

    private static ConfigManager NewConfig()
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.RepairEnable, ConfigValue = "true" },
            new ConfigItem { ConfigName = ConfigKeys.RepairPar2Enabled, ConfigValue = "true" },
        ]);
        return config;
    }

    private static DavItem NewItem() => new()
    {
        Id = Guid.NewGuid(),
        Name = "movie.mkv",
        Path = "/content/movie/movie.mkv",
        Type = DavItem.ItemType.UsenetFile,
        SubType = DavItem.ItemSubType.NzbFile,
    };

    private static string NewTempDirectory() =>
        Path.Join(Path.GetTempPath(), $"nzbdav-par2-admission-{Guid.NewGuid():N}");

    private static void DeleteTempDirectory(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { /* best effort */ }
    }
}
