using NzbWebDAV.Services.NativeCache;

namespace NzbWebDAV.Tests.Services;

public sealed class NativeFileSystemTests
{
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
