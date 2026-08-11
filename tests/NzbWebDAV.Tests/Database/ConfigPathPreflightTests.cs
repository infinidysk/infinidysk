using NzbWebDAV.Database;
using NzbWebDAV.Exceptions;

namespace NzbWebDAV.Tests.Database;

[Collection(nameof(ConfigPathCollection))]
public sealed class ConfigPathPreflightTests : IDisposable
{
    private readonly string _configRoot;
    private readonly string? _previousConfigPath;

    public ConfigPathPreflightTests()
    {
        _configRoot = Path.Join(Path.GetTempPath(), $"nzbdav-config-preflight-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_configRoot);
        _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
        Environment.SetEnvironmentVariable("CONFIG_PATH", _configRoot);
    }

    [Fact]
    public void VerifyAccess_PassesOnWritableConfigDirectory()
    {
        ConfigPathPreflight.VerifyAccess();
    }

    [Fact]
    public void VerifyAccess_PassesWithReadableStateFiles()
    {
        File.WriteAllText(Path.Join(_configRoot, "db.sqlite"), "open-only probe target");
        File.WriteAllText(Path.Join(_configRoot, "db.sqlite.maintenance.lock"), "");
        Directory.CreateDirectory(Path.Join(_configRoot, "blobs"));

        ConfigPathPreflight.VerifyAccess();
    }

    [Fact]
    public void VerifyAccess_CreatesMissingConfigDirectory()
    {
        var missing = Path.Join(_configRoot, "nested", "config");
        Environment.SetEnvironmentVariable("CONFIG_PATH", missing);

        ConfigPathPreflight.VerifyAccess();

        Assert.True(Directory.Exists(missing));
    }

    [SkippableFact]
    public void VerifyAccess_ThrowsForUnreadableLeaseFile()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix file modes are not enforced on Windows.");
        var lockPath = Path.Join(_configRoot, "db.sqlite.maintenance.lock");
        File.WriteAllText(lockPath, "left by root-owned install");
        SetMode(lockPath, UnixFileMode.None);
        Skip.IfNot(PermissionsEnforced(lockPath), "Test is running as root; file modes are not enforced.");

        var exception = Assert.Throws<ConfigPathAccessException>(() => ConfigPathPreflight.VerifyAccess());

        Assert.Contains(lockPath, exception.Message);
        Assert.Contains("chown", exception.Message);
    }

    [SkippableFact]
    public void VerifyAccess_ListsAllUnreadableStateFiles()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix file modes are not enforced on Windows.");
        var lockPath = Path.Join(_configRoot, "db.sqlite.maintenance.lock");
        var walPath = Path.Join(_configRoot, "db.sqlite-wal");
        File.WriteAllText(lockPath, "x");
        File.WriteAllText(walPath, "x");
        SetMode(lockPath, UnixFileMode.None);
        SetMode(walPath, UnixFileMode.None);
        Skip.IfNot(PermissionsEnforced(lockPath), "Test is running as root; file modes are not enforced.");

        var exception = Assert.Throws<ConfigPathAccessException>(() => ConfigPathPreflight.VerifyAccess());

        Assert.Contains(lockPath, exception.Message);
        Assert.Contains(walPath, exception.Message);
    }

    [SkippableFact]
    public void VerifyAccess_ThrowsForUnwritableConfigDirectory()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix file modes are not enforced on Windows.");
        SetMode(
            _configRoot,
            UnixFileMode.UserRead | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        Skip.IfNot(DirectoryWriteEnforced(_configRoot), "Test is running as root; file modes are not enforced.");

        var exception = Assert.Throws<ConfigPathAccessException>(() => ConfigPathPreflight.VerifyAccess());

        Assert.Contains(_configRoot, exception.Message);
    }

    // The guard keeps the platform analyzer satisfied; callers skip on Windows anyway.
    private static void SetMode(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, mode);
    }

    private static bool PermissionsEnforced(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            return false; // root bypasses mode bits
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool DirectoryWriteEnforced(string directory)
    {
        var probe = Path.Join(directory, $".probe-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
        try
        {
            if (Directory.Exists(_configRoot))
            {
                foreach (var file in Directory.EnumerateFiles(_configRoot))
                    SetMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                SetMode(
                    _configRoot,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            Directory.Delete(_configRoot, recursive: true);
        }
        catch (IOException)
        {
            // best effort cleanup
        }
        catch (UnauthorizedAccessException)
        {
            // best effort cleanup
        }
    }
}
