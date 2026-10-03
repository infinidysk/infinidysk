using NzbWebDAV.Services.NativeCache;

namespace NzbWebDAV.Tests.Services;

public sealed class NativeCacheProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "native-probe-" + Guid.NewGuid().ToString("N"));
    private NativeCacheFolder Folder()
    {
        var path = Path.Combine(_root, "cache");
        Directory.CreateDirectory(path);
        return new() { Id = "cache", Path = path, MinFreeBytes = 0 };
    }

    [Theory]
    [InlineData(0xEF53, "ext-family", "local")]
    [InlineData(0x58465342, "xfs", "local")]
    [InlineData(0x6969, "nfs", "nfs")]
    [InlineData(0xFF534D42L, "cifs", "smb")]
    [InlineData(0xFE534D42L, "smb2", "smb")]
    [InlineData(0x65735546, "fuse", "unknown")]
    [InlineData(1234, "unknown", "unknown")]
    public void FileSystemClassification_UsesDetectedMagic_NotStorageHint(long magic, string fileSystem, string capability)
        => Assert.Equal((fileSystem, capability), NativeFileSystem.ClassifyFileSystem(magic));

    [Fact]
    public async Task WritableProbe_VerifiesDurableRoundTrip_AndRemovesOnlyItsTemporaryFile()
    {
        var folder = Folder();
        var called = false;
        await using var store = new NativeCacheStore(Path.Combine(_root, "index.db"), [folder])
        {
            BeforeProbeReadAsync = (directory, name, _) =>
            {
                using var file = directory.OpenFile(name, FileMode.Open, FileAccess.Read);
                Assert.Equal(4096, file.Length);
                called = true;
                return Task.CompletedTask;
            }
        };
        var result = await store.ProbeAsync(folder.Id);
        Assert.True(called);
        Assert.True(result.Readable);
        Assert.True(result.Writable);
        Assert.True(result.DurableWriteVerified);
        Assert.Null(result.Error);
        Assert.True(result.AvailableBytes > 0);
        Assert.Empty(Directory.GetFiles(folder.Path, ".infinidysk-probe-*"));
        Assert.Equal(0, (await store.GetStatusAsync()).Single().Entries);
    }

    [Fact]
    public async Task Probe_DetectsCorruptReadback_AndCleansTemporaryFile()
    {
        var folder = Folder();
        await using var store = new NativeCacheStore(Path.Combine(_root, "index.db"), [folder])
        {
            BeforeProbeReadAsync = (directory, name, _) =>
            {
                using var file = directory.OpenFile(name, FileMode.Open, FileAccess.ReadWrite);
                var original = file.ReadByte();
                file.Position = 0;
                file.WriteByte((byte)(original ^ 0xff));
                file.Flush(true);
                return Task.CompletedTask;
            }
        };
        var result = await store.ProbeAsync(folder.Id);
        Assert.True(result.Readable);
        Assert.False(result.DurableWriteVerified);
        Assert.NotNull(result.Error);
        Assert.Empty(Directory.GetFiles(folder.Path, ".infinidysk-probe-*"));
    }

    [Fact]
    public async Task ReadOnlyProbe_DoesNotAttemptMutation()
    {
        var folder = Folder();
        var catalogue = Path.Combine(_root, "index.db");
        await using (var initial = new NativeCacheStore(catalogue, [folder])) { }
        var before = Directory.GetFileSystemEntries(folder.Path).Order().ToArray();
        await using var store = new NativeCacheStore(catalogue, [folder with { ReadOnly = true }])
        { BeforeProbeReadAsync = (_, _, _) => throw new InvalidOperationException("Readonly probe must not write.") };
        var result = await store.ProbeAsync(folder.Id);
        Assert.True(result.Readable);
        Assert.False(result.Writable);
        Assert.False(result.DurableWriteVerified);
        Assert.Null(result.Error);
        Assert.Equal(before, Directory.GetFileSystemEntries(folder.Path).Order());
    }

    [Fact]
    public async Task Probe_InsufficientReserve_DoesNotWrite()
    {
        var folder = Folder();
        await using var store = new NativeCacheStore(Path.Combine(_root, "index.db"), [folder]) { AvailableBytesOverride = _ => 0 };
        var result = await store.ProbeAsync(folder.Id);
        Assert.True(result.Readable);
        Assert.False(result.Writable);
        Assert.NotNull(result.Error);
        Assert.Empty(Directory.GetFiles(folder.Path, ".infinidysk-probe-*"));
    }

    [Fact]
    public async Task CancelledProbe_CleansItsTemporaryFile()
    {
        var folder = Folder();
        using var cancellation = new CancellationTokenSource();
        await using var store = new NativeCacheStore(Path.Combine(_root, "index.db"), [folder])
        {
            BeforeProbeReadAsync = (_, _, ct) =>
            {
                cancellation.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ProbeAsync(folder.Id, cancellation.Token));
        Assert.Empty(Directory.GetFiles(folder.Path, ".infinidysk-probe-*"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Probe_RootReplacedMidIo_NeverTouchesReplacement(bool symlink)
    {
        var folder = Folder();
        var moved = folder.Path + "-original";
        var replacement = symlink ? folder.Path + "-replacement" : folder.Path;
        await using var store = new NativeCacheStore(Path.Combine(_root, "index.db"), [folder])
        {
            BeforeProbeReadAsync = (_, _, _) =>
            {
                Directory.Move(folder.Path, moved);
                Directory.CreateDirectory(replacement);
                if (symlink) Directory.CreateSymbolicLink(folder.Path, replacement);
                return Task.CompletedTask;
            }
        };
        var result = await store.ProbeAsync(folder.Id);
        Assert.False(result.DurableWriteVerified);
        Assert.NotNull(result.Error);
        Assert.Empty(Directory.GetFileSystemEntries(replacement));
        Assert.Empty(Directory.GetFiles(moved, ".infinidysk-probe-*"));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
