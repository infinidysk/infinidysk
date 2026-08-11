using System.Collections.Concurrent;
using NzbWebDAV.Exceptions;
using Serilog;

namespace NzbWebDAV.Database;

/// <summary>
/// Serializes all NzbDAV database-maintenance processes before they recover
/// EF's non-crash-safe SQLite migration lock.
/// </summary>
internal sealed class DatabaseMigrationLease : IAsyncDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> InProcessLocks = new(
        StringComparer.Ordinal);

    private readonly FileStream _stream;
    private readonly SemaphoreSlim _inProcessLock;
    private bool _disposed;

    private DatabaseMigrationLease(FileStream stream, SemaphoreSlim inProcessLock)
    {
        _stream = stream;
        _inProcessLock = inProcessLock;
    }

    public static async Task<DatabaseMigrationLease> AcquireAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        var fullDatabasePath = Path.GetFullPath(databasePath);
        var leasePath = fullDatabasePath + ".maintenance.lock";
        var directory = Path.GetDirectoryName(leasePath)
            ?? throw new InvalidOperationException($"Database path has no parent directory: {databasePath}");
        Directory.CreateDirectory(directory);

        var inProcessLock = InProcessLocks.GetOrAdd(
            leasePath,
            static _ => new SemaphoreSlim(1, 1));
        await inProcessLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        FileStream? stream = null;
        var loggedWait = false;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    stream = new FileStream(
                        leasePath,
                        new FileStreamOptions
                        {
                            Mode = FileMode.OpenOrCreate,
                            Access = FileAccess.ReadWrite,
                            Share = FileShare.None,
                            Options = FileOptions.Asynchronous,
                        });
                    return new DatabaseMigrationLease(stream, inProcessLock);
                }
                catch (UnauthorizedAccessException unauthorized)
                {
                    // Not an IOException, so it never reaches the retry loop below.
                    // Surface it as an actionable config-path error instead of an
                    // unhandled core-dump crash.
                    throw ConfigPathAccessException.ForPath(
                        leasePath,
                        Path.GetDirectoryName(leasePath) ?? leasePath,
                        unauthorized);
                }
                catch (IOException)
                {
                    if (stream is not null)
                        await stream.DisposeAsync().ConfigureAwait(false);
                    stream = null;
                    if (!loggedWait)
                    {
                        Log.Information(
                            "Waiting for another NzbDAV process to finish database maintenance");
                        loggedWait = true;
                    }

                    await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            if (stream is not null)
                await stream.DisposeAsync().ConfigureAwait(false);
            inProcessLock.Release();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _inProcessLock.Release();
        }
    }
}
