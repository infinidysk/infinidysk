using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public sealed class NzbBackupRetentionServiceTests : IDisposable
{
    private readonly string _backupRoot =
        Path.Join(Path.GetTempPath(), $"nzbdav-nzb-backup-retention-{Guid.NewGuid():N}");

    public NzbBackupRetentionServiceTests()
    {
        Directory.CreateDirectory(Path.Join(_backupRoot, "tv"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_backupRoot, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void SweepDirectory_DeletesAgedNzbFiles_KeepsRecent()
    {
        var oldPath = Path.Join(_backupRoot, "tv", "old.nzb");
        var recentPath = Path.Join(_backupRoot, "tv", "recent.nzb");
        var otherPath = Path.Join(_backupRoot, "tv", "notes.txt");
        File.WriteAllText(oldPath, "old");
        File.WriteAllText(recentPath, "recent");
        File.WriteAllText(otherPath, "keep");
        File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddDays(-40));
        File.SetLastWriteTimeUtc(recentPath, DateTime.UtcNow.AddDays(-5));

        var deleted = NzbBackupRetentionService.SweepDirectory(_backupRoot, retentionDays: 30, DateTime.UtcNow);

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(oldPath));
        Assert.True(File.Exists(recentPath));
        Assert.True(File.Exists(otherPath));
    }

    [Fact]
    public void SweepDirectory_WithZeroRetention_DeletesNothing()
    {
        var path = Path.Join(_backupRoot, "tv", "old.nzb");
        File.WriteAllText(path, "old");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-400));

        var deleted = NzbBackupRetentionService.SweepDirectory(_backupRoot, retentionDays: 0, DateTime.UtcNow);

        Assert.Equal(0, deleted);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void SweepDirectory_RefusesRootOrEmpty()
    {
        Assert.Equal(0, NzbBackupRetentionService.SweepDirectory("/", 30, DateTime.UtcNow));
        Assert.Equal(0, NzbBackupRetentionService.SweepDirectory("", 30, DateTime.UtcNow));
        Assert.Equal(0, NzbBackupRetentionService.SweepDirectory("   ", 30, DateTime.UtcNow));
    }
}
