using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NzbWebDAV.Services.NativeCache;

/// <summary>Linux native-cache durability/allocation helpers. Never count sparse logical length as allocation.</summary>
public static class NativeFileSystem
{
    /// <summary>Pin each path component without following links. Payload IO must use this handle, never the configured pathname.</summary>
    public static PinnedDirectory PinDirectory(string path)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Native cache directory anchoring requires Linux.");
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("A directory anchor requires an absolute path.");
        var root = new PinnedDirectory(Own(Open("/", DirectoryFlags)));
        try { return root.OpenDirectoryCore(Path.GetFullPath(path).Trim('/'), create: false, enforceMount: false); }
        finally { root.Dispose(); }
    }

    private static int DirectoryFlags => GetOpenFlags(RuntimeInformation.ProcessArchitecture).Directory
        | GetOpenFlags(RuntimeInformation.ProcessArchitecture).NoFollow | 0x80000;
    internal static (int Directory, int NoFollow) GetOpenFlags(Architecture architecture) => architecture switch
    {
        Architecture.X64 => (0x10000, 0x20000),
        Architecture.Arm64 => (0x4000, 0x8000),
        _ => throw new PlatformNotSupportedException("Native cache supports Linux x64 and arm64 only.")
    };
    private sealed class DescriptorException(int error) : IOException("Native cache descriptor operation failed.")
    {
        public int Error { get; } = error;
    }
    internal static bool IsMissing(IOException exception) => exception is DescriptorException { Error: 2 };
    private static SafeFileHandle Own(int descriptor) => descriptor >= 0
        ? new SafeFileHandle(descriptor, ownsHandle: true)
        : throw new DescriptorException(Marshal.GetLastPInvokeError());

    public sealed class PinnedDirectory : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private readonly StatxResult _identity;
        internal PinnedDirectory(SafeFileHandle handle)
        {
            _handle = handle;
            try { _identity = Stat(handle); }
            catch { handle.Dispose(); throw; }
        }

        public string DeviceIdentity => $"{_identity.DeviceMajor}:{_identity.DeviceMinor}";
        // Device numbers can change after a legitimate remount. A mismatch fails
        // closed until the administrator explicitly registers a new folder ID.
        public string RegistrationIdentity => $"{DeviceIdentity}:{_identity.Inode}";

        public long AvailableBytes
        {
            get
            {
                if (StatVfs(_handle, out var result) != 0) throw new IOException("Cannot inspect native cache free space.");
                var bytes = (UInt128)result.FragmentSize * result.AvailableBlocks;
                return bytes > long.MaxValue ? long.MaxValue : (long)bytes;
            }
        }

        public bool IsCurrent(string path)
        {
            try
            {
                using var current = PinDirectory(path);
                return current._identity.Inode == _identity.Inode && current.DeviceIdentity == DeviceIdentity
                    && current._identity.MountId == _identity.MountId;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        public PinnedDirectory OpenDirectory(string relative, bool create = false)
            => OpenDirectoryCore(relative, create, enforceMount: true);

        internal PinnedDirectory OpenDirectoryCore(string relative, bool create, bool enforceMount)
        {
            var parts = Components(relative);
            SafeFileHandle? child = null;
            try
            {
                foreach (var part in parts)
                {
                    var parent = child ?? _handle;
                    if (create && MkdirAt(parent, part, 0x1c0) != 0 && Marshal.GetLastPInvokeError() != 17)
                        throw new IOException("Cannot create native cache directory.");
                    var next = Own(OpenAt(parent, part, DirectoryFlags, 0));
                    child?.Dispose();
                    child = next;
                    var identity = Stat(child);
                    if (enforceMount && (identity.DeviceMajor != _identity.DeviceMajor || identity.DeviceMinor != _identity.DeviceMinor
                        || identity.MountId != _identity.MountId))
                        throw new IOException("Native cache descendants cannot cross filesystem or mount boundaries.");
                }
                child ??= Own(OpenAt(_handle, ".", DirectoryFlags, 0));
                return new PinnedDirectory(child);
            }
            catch { child?.Dispose(); throw; }
        }

        public FileStream OpenFile(string name, FileMode mode, FileAccess access, bool exclusive = false)
        {
            Leaf(name);
            var flags = GetOpenFlags(RuntimeInformation.ProcessArchitecture).NoFollow | 0x80000 | 0x800;
            flags |= access switch { FileAccess.Read => 0, FileAccess.Write => 1, _ => 2 };
            flags |= mode switch
            {
                FileMode.CreateNew => 0x40 | 0x80,
                FileMode.Create or FileMode.OpenOrCreate or FileMode.Append => 0x40,
                _ => 0
            };
            var handle = Own(OpenAt(_handle, name, flags, 0x180));
            try
            {
                var info = Stat(handle);
                if ((info.Mode & 0xf000) != 0x8000 || info.Links != 1)
                    throw new IOException("Native cache leaves must be regular, unlinked files.");
                if (info.DeviceMajor != _identity.DeviceMajor || info.DeviceMinor != _identity.DeviceMinor || info.MountId != _identity.MountId)
                    throw new IOException("Native cache files cannot cross filesystem or mount boundaries.");
                if (exclusive && Flock(handle, 2 | 4) != 0) throw new IOException("Native cache folder already has an owner.");
                var stream = new FileStream(handle, access, 4096, isAsync: false);
                try
                {
                    // Never truncate before validating the descriptor's type.
                    if (mode is FileMode.Create or FileMode.Truncate) stream.SetLength(0);
                    if (mode == FileMode.Append) stream.Position = stream.Length;
                    return stream;
                }
                catch { stream.Dispose(); throw; }
            }
            catch { handle.Dispose(); throw; }
        }

        public long AllocatedBytes(string name)
        {
            using var file = OpenFile(name, FileMode.Open, FileAccess.Read);
            return checked((long)Stat(file.SafeFileHandle).Blocks * 512);
        }

        public void Flush()
        {
            if (FsyncHandle(_handle) != 0) throw new IOException("Cannot flush native cache directory metadata.");
        }

        public IEnumerable<string> EnumerateDirectoryNames()
        {
            // /proc resolves our live descriptor, not the configured mount pathname.
            // Names are untrusted: callers must open them through OpenDirectory.
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            return Directory.EnumerateDirectories($"/proc/self/fd/{_handle.DangerousGetHandle()}")
                .Select(path => Path.GetFileName(path));
        }

        public void DeleteFile(string name)
        {
            Leaf(name);
            if (UnlinkAt(_handle, name, 0) != 0 && Marshal.GetLastPInvokeError() != 2)
                throw new IOException("Cannot remove native cache file.");
        }

        public void DeleteDirectory(string name)
        {
            Leaf(name);
            if (UnlinkAt(_handle, name, 0x200) != 0 && Marshal.GetLastPInvokeError() != 2)
                throw new IOException("Cannot remove native cache directory.");
        }

        private static string[] Components(string path)
        {
            if (path.Length == 0) return [];
            var parts = path.Split('/');
            foreach (var part in parts) Leaf(part);
            return parts;
        }
        private static void Leaf(string name)
        {
            if (string.IsNullOrEmpty(name) || name is "." or ".." || name.Contains('/') || name.Contains('\0'))
                throw new ArgumentException("Native cache paths must contain only relative, non-traversing components.");
        }
        public void Dispose() => _handle.Dispose();
    }

    private static StatxResult Stat(SafeFileHandle handle)
    {
        if (StatxHandle(handle, "", 0x1000, 0x17ff, out var result) != 0 || (result.Mask & 0x1505) != 0x1505)
            throw new IOException("Cannot inspect native cache descriptor.");
        return result;
    }
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
        var descriptor = Open(path, DirectoryFlags);
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
        [FieldOffset(16)] public uint Links;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(48)] public ulong Blocks;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
        [FieldOffset(144)] public ulong MountId;
    }

    // Linux x64/arm64 libc statvfs begins with five unsigned-long fields. Reserve
    // more than the ABI's tail size; never read platform-dependent tail members.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatVfsResult
    {
        [FieldOffset(8)] public ulong FragmentSize;
        [FieldOffset(32)] public ulong AvailableBlocks;
    }

    [DllImport("libc", EntryPoint = "fstatvfs", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int StatVfs(SafeFileHandle descriptor, out StatVfsResult result);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int OpenAt(SafeFileHandle directory, string path, int flags, uint mode);
    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int MkdirAt(SafeFileHandle directory, string path, uint mode);
    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int UnlinkAt(SafeFileHandle directory, string path, int flags);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int StatxHandle(SafeFileHandle directory, string path, int flags, uint mask, out StatxResult result);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int FsyncHandle(SafeFileHandle descriptor);
    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Flock(SafeFileHandle descriptor, int operation);

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
