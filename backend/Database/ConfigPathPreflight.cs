using NzbWebDAV.Exceptions;

namespace NzbWebDAV.Database;

/// <summary>
/// Fails fast with one actionable error when CONFIG_PATH is missing, is not a
/// directory, or a known state file under it is not readable/writable by the
/// process user, instead of aborting later with an unhandled
/// UnauthorizedAccessException (which core-dumps and hides the cause).
/// Does not create a missing CONFIG_PATH: forgotten volume mounts should fail
/// rather than write into an ephemeral container directory.
/// Best-effort early report: a file can become unreadable between this probe and the
/// real open, so the open sites (e.g. DatabaseMigrationLease) remain the backstop.
/// </summary>
internal static class ConfigPathPreflight
{
    // SQLite state files the backend reads and writes under CONFIG_PATH.
    private static readonly string[] KnownDatabaseFiles =
    [
        "db.sqlite",
        "metrics.sqlite",
        "warden.db",
        "usenet-migration.db",
    ];

    // Directories created lazily by the backend; only probed when they already exist.
    private static readonly string[] KnownDirectories =
    [
        "blobs",
        "data-protection",
        "migration-backups",
        "backups",
        "restore-staging",
    ];

    private static readonly string[] KnownStateFiles =
    [
        "pending-restore.json",
    ];

    public static void VerifyAccess()
    {
        var configPath = DavDatabaseContext.ConfigPath;
        var failures = new List<string>();

        if (File.Exists(configPath))
        {
            failures.Add($"{configPath} (path exists but is not a directory)");
        }
        else if (!Directory.Exists(configPath))
        {
            failures.Add(
                $"{configPath} (directory does not exist; create or mount it before startup, typically as /config)");
        }
        else if (!CanWriteToDirectory(configPath))
        {
            failures.Add($"{configPath} (directory is not writable)");
        }

        if (Directory.Exists(configPath))
        {
            var databaseFiles = DatabaseProviderConfig.IsPostgres
                ? KnownDatabaseFiles.Where(fileName => fileName != "db.sqlite")
                : KnownDatabaseFiles.AsEnumerable();

            foreach (var path in databaseFiles.Select(fileName => Path.Join(configPath, fileName)))
            {
                ProbeFile(path, failures);
                ProbeFile(path + "-wal", failures);
                ProbeFile(path + "-shm", failures);
            }

            if (!DatabaseProviderConfig.IsPostgres)
                ProbeFile(DavDatabaseContext.DatabaseFilePath + ".maintenance.lock", failures);

            foreach (var path in KnownDirectories.Select(directory => Path.Join(configPath, directory)))
            {
                if (Directory.Exists(path) && !CanWriteToDirectory(path))
                    failures.Add($"{path} (directory is not writable)");
            }

            foreach (var path in KnownStateFiles.Select(fileName => Path.Join(configPath, fileName)))
                ProbeFile(path, failures);
        }

        if (failures.Count > 0)
            throw ConfigPathAccessException.ForPaths(configPath, failures);
    }

    private static void ProbeFile(string path, List<string> failures)
    {
        if (!File.Exists(path)) return;

        // Plain FileStream only: never open SQLite databases through EF here, so the
        // probe cannot create WAL/SHM sidecars, hold locks, or touch the schema.
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        }
        catch (UnauthorizedAccessException)
        {
            failures.Add(path);
        }
        catch (IOException)
        {
            // Locked or busy is not a permission problem; the real open sites
            // already handle those with retries.
        }
    }

    private static bool CanWriteToDirectory(string directory)
    {
        var probe = Path.Join(directory, $".config-path-probe-{Guid.NewGuid():N}");
        try
        {
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
            }

            File.Delete(probe);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
