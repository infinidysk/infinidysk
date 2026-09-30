using NzbWebDAV.Services.NativeCache;
using System.Runtime.InteropServices;

namespace NzbWebDAV.Tests.Services;

public sealed class NativeFileSystemTests
{
    [Theory]
    [InlineData(Architecture.X64, 0x10000, 0x20000)]
    [InlineData(Architecture.Arm64, 0x4000, 0x8000)]
    public void LinuxOpenFlags_MatchEachSupportedArchitecture(Architecture architecture, int directory, int noFollow)
        => Assert.Equal((directory, noFollow), NativeFileSystem.GetOpenFlags(architecture));

    [Fact]
    public void LinuxOpenFlags_UnsupportedArchitectureFailsClosed()
        => Assert.Throws<PlatformNotSupportedException>(() => NativeFileSystem.GetOpenFlags(Architecture.X86));

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void PinnedRoot_PathReplacementCannotRedirectWrites(bool symlink)
    {
        Skip.IfNot(OperatingSystem.IsLinux());
        var parent = Path.Combine(Path.GetTempPath(), "native-anchor-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "cache");
        var moved = Path.Combine(parent, "original");
        var replacement = Path.Combine(parent, "replacement");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(replacement);
        try
        {
            using var pinned = NativeFileSystem.PinDirectory(root);
            Assert.True(pinned.IsCurrent(root));
            Directory.Move(root, moved);
            if (symlink) Directory.CreateSymbolicLink(root, replacement);
            else Directory.CreateDirectory(root);
            Assert.False(pinned.IsCurrent(root));
            using var entry = pinned.OpenDirectory("v1/ab/entry", create: true);
            using var data = entry.OpenFile("content.data", FileMode.CreateNew, FileAccess.Write);
            data.WriteByte(42);
            Assert.Empty(Directory.EnumerateFileSystemEntries(replacement));
            if (!symlink) Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            Assert.True(File.Exists(Path.Combine(moved, "v1/ab/entry/content.data")));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    [SkippableFact]
    public void PinnedRoot_RejectsSymlinkComponentsAndNonregularLeaves()
    {
        Skip.IfNot(OperatingSystem.IsLinux());
        var root = Path.Combine(Path.GetTempPath(), "native-links-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var pinned = NativeFileSystem.PinDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "outside"));
            Directory.CreateSymbolicLink(Path.Combine(root, "redirect"), Path.Combine(root, "outside"));
            Assert.ThrowsAny<IOException>(() => pinned.OpenDirectory("redirect/child", create: true));
            File.CreateSymbolicLink(Path.Combine(root, "data"), Path.Combine(root, "outside/data"));
            Assert.ThrowsAny<IOException>(() => pinned.OpenFile("data", FileMode.OpenOrCreate, FileAccess.Write));
            Assert.Throws<ArgumentException>(() => pinned.OpenDirectory("../escape", create: true));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(root, "outside")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [SkippableFact]
    public void SparseAccounting_UsesAllocatedBlocks_NotLogicalLength()
    {
        Skip.IfNot(OperatingSystem.IsLinux());
        var directory = Path.Combine(Path.GetTempPath(), "native-allocated-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "sparse.data");
        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew))
            {
                stream.Position = 64 * 1024 * 1024;
                stream.WriteByte(1);
                stream.Flush(true);
            }
            var allocated = NativeFileSystem.GetAllocatedBytes(path);
            Assert.InRange(allocated, 512, 1024 * 1024);
            NativeFileSystem.FlushDirectory(directory);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
