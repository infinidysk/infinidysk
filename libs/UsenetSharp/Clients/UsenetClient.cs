using System.IO.Pipelines;
using RapidYencSharp;

namespace UsenetSharp.Clients;

public partial class UsenetClient : IUsenetClient, IDisposable, IAsyncDisposable
{
    private int _disposeState;

    static UsenetClient()
    {
        // Native *_init mutates global dispatch state and is not thread-safe across
        // subsystems. Serialize encode/decode/CRC init before any concurrent first use.
        YencEncoder.EnsureInitialized();
        YencDecoder.EnsureInitialized();
        Crc32.EnsureInitialized();
    }

    public UsenetClient()
        : this(new UsenetClientOptions(), TimeProvider.System)
    {
    }

    public UsenetClient(UsenetClientOptions options)
        : this(options, TimeProvider.System)
    {
    }

    internal UsenetClient(UsenetClientOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (options.ReadTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), "ReadTimeout must be greater than zero.");
        }

        if (options.TcpKeepAliveTime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), "TcpKeepAliveTime must be greater than zero.");
        }

        if (options.TcpKeepAliveInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), "TcpKeepAliveInterval must be greater than zero.");
        }

        if (options.TcpKeepAliveRetryCount < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), "TcpKeepAliveRetryCount must be at least 1.");
        }

        if (options.AbandonedBodyDrainLimit < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), "AbandonedBodyDrainLimit cannot be negative.");
        }

        if (options.DecodedBodyPauseWriterThreshold <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), "DecodedBodyPauseWriterThreshold must be greater than zero.");
        }

        if (options.DecodedBodyResumeWriterThreshold < 0 ||
            options.DecodedBodyResumeWriterThreshold > options.DecodedBodyPauseWriterThreshold)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "DecodedBodyResumeWriterThreshold must be non-negative and cannot exceed " +
                "DecodedBodyPauseWriterThreshold.");
        }

        if (options.MaxPipelineDepth < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), "MaxPipelineDepth must be at least 1.");
        }

        if (!Enum.IsDefined(options.CertificateRevocationCheckMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), "CertificateRevocationCheckMode must be a defined X509RevocationMode value.");
        }

        _options = options;
        _timeProvider = timeProvider;
        _decodedBodyPipeOptions = new PipeOptions(
            pauseWriterThreshold: options.DecodedBodyPauseWriterThreshold,
            resumeWriterThreshold: options.DecodedBodyResumeWriterThreshold,
            minimumSegmentSize: DecodedBodyChunkSize,
            useSynchronizationContext: false);
    }

    /// <summary>
    /// Gets decoded BODY bytes written to this client's pipes but not yet read or discarded.
    /// </summary>
    public long BufferedDecodedBodyBytes => Interlocked.Read(ref _bufferedDecodedBodyBytes);

    public bool IsConnected =>
        Volatile.Read(ref _disposeState) == 0 &&
        Volatile.Read(ref _connectionState) != 0;

    public bool IsHealthy
    {
        get
        {
            if (!IsConnected)
            {
                return false;
            }

            return _backgroundException == null;
        }
    }

    public async Task WaitForReadyAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
        }
        finally
        {
            _commandLock.Release();
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing) return;
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        CancelConnectionLifetime();
        _commandLock.WaitAsync().GetAwaiter().GetResult();
        CleanupConnection(createNewLifetime: false);
        // Dispose the semaphore while still holding it so queued waiters are
        // faulted instead of being granted a lock that is about to be disposed.
        _commandLock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        try
        {
            await TryQuitBestEffortAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort only; disposal must proceed.
        }

        CancelConnectionLifetime();
        await _commandLock.WaitAsync().ConfigureAwait(false);
        CleanupConnection(createNewLifetime: false);
        // Dispose the semaphore while still holding it so queued waiters are
        // faulted instead of being granted a lock that is about to be disposed.
        _commandLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
