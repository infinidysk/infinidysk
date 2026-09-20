using System.Runtime.InteropServices;

namespace NzbWebDAV.Services.NativeCache;

/// <summary>Linux native-cache durability/allocation helpers. Never count sparse logical length as allocation.</summary>
public static class NativeFileSystem
{
    public static long GetAllocatedBytes(string path)
    {
        if (!OperatingSystem.IsLinux()) return new FileInfo(path).Length;
        // Linux UAPI struct statx is architecture-independent, 256 bytes. stx_blocks
        // at offset 0x30 is in 512-byte units (include/linux/stat.h).
        if (Statx(-100, path, 0x100, 0x400, out var result) != 0 || (result.Mask & 0x400) == 0)
            throw new IOException("Cannot determine allocated native cache bytes.");
        return checked((long)result.Blocks * 512);
    }

    public static void FlushDirectory(string path)
    {
        if (!OperatingSystem.IsLinux()) return;
        // O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC, Linux generic UAPI.
        var descriptor = Open(path, 0x10000 | 0x20000 | 0x80000);
        if (descriptor < 0) throw new IOException("Cannot open native cache directory for durable publication.");
        try
        {
            if (Fsync(descriptor) != 0) throw new IOException("Cannot flush native cache directory metadata.");
        }
        finally { _ = Close(descriptor); }
    }

    public static void RequireLocalMetadata(string path)
    {
        var absolute = Path.GetFullPath(path);
        for (var component = new DirectoryInfo(absolute); component is not null; component = component.Parent)
            if (component.LinkTarget is not null)
                throw new ArgumentException("Native cache metadata paths cannot contain symbolic links; use a direct local path.");
        var drive = DriveInfo.GetDrives().Where(drive => absolute == Path.TrimEndingDirectorySeparator(drive.Name) || absolute.StartsWith(
            drive.Name.EndsWith(Path.DirectorySeparatorChar) ? drive.Name : drive.Name + Path.DirectorySeparatorChar,
            StringComparison.Ordinal)).MaxBy(drive => drive.Name.Length);
        if (drive is not null && (drive.DriveType == DriveType.Network
            || drive.DriveFormat is "nfs" or "nfs4" or "cifs" or "smb3" or "fuse.sshfs"))
            throw new ArgumentException("Native cache metadata requires local storage; do not place the SQLite catalogue on NAS/NFS/SMB.");
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxResult
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(48)] public ulong Blocks;
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Statx(int directory, string path, int flags, uint mask, out StatxResult result);

    [DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Open(string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Fsync(int descriptor);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Close(int descriptor);
}
