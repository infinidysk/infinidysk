using System.Diagnostics;
using NzbWebDAV.Exceptions;
using Serilog;

namespace NzbWebDAV.Services.Catalog;

/// <summary>
/// One long-running FFmpeg invocation that encodes a contiguous span of the source as an
/// HLS event playlist, publishing each six-second segment as it is completed.
/// <para>
/// Segments produced this way share a single encoder and a single timestamp timeline, so
/// audio and video meet at every boundary without the priming hole, re-quantisation step, or
/// decoder re-initialisation that independent per-segment encodes introduce. Seeking starts
/// a new run at the requested boundary; older runs are stopped once their output is no
/// longer wanted.
/// </para>
/// </summary>
internal sealed class CatalogTranscodeRun : IAsyncDisposable
{
    private Task _completion = Task.CompletedTask;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<SegmentPublication> _publications = [];
    private readonly Lock _gate = new();
    private bool _failed;

    private CatalogTranscodeRun()
    {
    }

    /// <summary>Absolute source position of this run's first segment.</summary>
    public TimeSpan Start { get; private init; }

    /// <summary>Number of segments this run will publish.</summary>
    public int Count { get; private init; }

    public static CatalogTranscodeRun StartNew(
        IFFmpegRunner runner,
        string sourceUri,
        TimeSpan start,
        int count,
        string directory,
        Func<int, string> pathFor)
    {
        var run = new CatalogTranscodeRun
        {
            Start = start,
            Count = count,
        };
        run._completion = run.EncodeAsync(runner, sourceUri, directory, pathFor);
        return run;
    }

    /// <summary>
    /// Waits for segment <paramref name="index"/> (relative to this run) to be published and
    /// returns its path. A failed run fails every waiter so the caller can retry.
    /// </summary>
    public async Task<string> WaitForSegmentAsync(int index, CancellationToken ct)
    {
        if (index < 0 || index >= Count)
            throw CatalogException.NotFound(CatalogTranscodeService.SegmentNotFoundMessage);

        TaskCompletionSource<string> waiter;
        lock (_gate)
        {
            var publication = GetOrAddPublication(index);
            if (publication.Path is { } ready) return ready;
            ThrowIfFailed();
            waiter = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            publication.Waiters.Add(waiter);
        }

        return await waiter.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Stops the encoder and releases every waiter.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _completion.ConfigureAwait(false);
        }
        catch
        {
            // The failure has already been published to waiters.
        }

        _stop.Dispose();
    }

    private async Task EncodeAsync(
        IFFmpegRunner runner,
        string sourceUri,
        string directory,
        Func<int, string> pathFor)
    {
        try
        {
            await runner.TranscodeRangeAsync(
                    sourceUri,
                    Start,
                    Count,
                    directory,
                    (index, path) => Publish(index, pathFor(index)),
                    _stop.Token)
                .ConfigureAwait(false);

            lock (_gate)
            {
                // Anything still pending means the encoder ended early; release the waiters.
                foreach (var publication in _publications)
                {
                    if (publication.Path is not null) continue;
                    var error = CatalogException.Provider(CatalogTranscodeService.SegmentFailedMessage);
                    foreach (var waiter in publication.Waiters) waiter.TrySetException(error);
                    publication.Waiters.Clear();
                }
            }
        }
        catch (Exception e)
        {
            Fail(e is OperationCanceledException
                ? CatalogException.Provider(CatalogTranscodeService.SegmentFailedMessage)
                : e);
        }
    }

    private void Publish(int index, string path)
    {
        lock (_gate)
        {
            var publication = GetOrAddPublication(index);
            publication.Path = path;
            foreach (var waiter in publication.Waiters) waiter.TrySetResult(path);
            publication.Waiters.Clear();
        }
    }

    private void Fail(Exception error)
    {
        lock (_gate)
        {
            _failed = true;
            foreach (var publication in _publications)
            {
                if (publication.Path is not null) continue;
                foreach (var waiter in publication.Waiters) waiter.TrySetException(error);
                publication.Waiters.Clear();
            }
        }
    }

    private void ThrowIfFailed()
    {
        if (_failed) throw CatalogException.Provider(CatalogTranscodeService.SegmentFailedMessage);
    }

    private SegmentPublication GetOrAddPublication(int index)
    {
        while (_publications.Count <= index) _publications.Add(new SegmentPublication());
        return _publications[index];
    }

    private sealed class SegmentPublication
    {
        public string? Path { get; set; }
        public List<TaskCompletionSource<string>> Waiters { get; } = [];
    }
}
