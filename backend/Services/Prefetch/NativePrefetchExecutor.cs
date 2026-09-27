using NzbWebDAV.Services.NativeCache;
using NzbWebDAV.Streams;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.WebDav.Base;

namespace NzbWebDAV.Services.Prefetch;

public sealed class PrefetchDeferredException(string message, bool countsAsFailure = false) : Exception(message)
{
    public bool CountsAsFailure { get; } = countsAsFailure;
}

public sealed class NativePrefetchExecutor(IServiceScopeFactory scopes, NativeCacheService native, ConfigManager config,
    PrefetchJobStore jobs, ActiveReadRegistry activeReads, PlexPlaybackRegistry? playback = null,
    Func<PrefetchSettings>? settingsProvider = null) : IPrefetchExecutor
{
    private PrefetchSettings Settings() => settingsProvider?.Invoke()
        ?? PrefetchSettings.Parse(config.GetEffectiveConfigValue(ConfigKeys.SmartPrefetchSettings));
    public async Task ExecuteAsync(PrefetchJob job, CancellationToken ct)
    {
        var settings = Settings();
        if (native.Store is null) throw new PrefetchDeferredException("Native cache must be active before warming.");
        using var scope = scopes.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();
        var item = await database.GetFileById(job.ItemId.ToString()).ConfigureAwait(false)
            ?? throw new ArgumentException("The imported media file no longer exists.");
        if (item.FileSize is not > 0 || item.FileSize > settings.MaxBytesPerItem)
            throw new ArgumentException("The media length is unavailable or exceeds the per-file warming cap.");
        if (item.FileSize < NativeCacheSettings.MinimumFileBytes(config))
            throw new ArgumentException("The media file is below the configured Native Cache minimum size.");
        var factory = scope.ServiceProvider.GetRequiredService<IDavContentStreamFactory>();
        await using var wireBudget = new PrefetchWireBudget(jobs, () => Settings().DailyByteBudget, ct);
        using var attribution = wireBudget.Enter();
        try
        {
        await using var readScope = BaseStoreStreamFile.BeginReadScope(config, scope.ServiceProvider,
            SemaphorePriority.Low, settings.ConnectionsPerJob, wireBudget.Token);
        Stream admitted;
        try { admitted = await native.WrapAsync(item, token => factory.OpenAsync(item, token), wireBudget.Token, requireNative: true).ConfigureAwait(false); }
        catch (InvalidOperationException) { throw new PrefetchDeferredException("Native cache buffers or metadata are unavailable."); }
        await using var stream = admitted;
        var cached = (NativeCachedStream)stream;
        bool CanContinue()
        {
            var current = Settings();
            return !jobs.Paused && jobs.IsRunning(job.Id)
                && item.FileSize <= current.MaxBytesPerItem
                && (!current.PauseDuringPlayback || activeReads.Snapshot().Count == 0 && playback?.HasActivePlayback != true);
        }
        await WarmAsync(native.Store, cached, job.Start, job.Length,
            async _ => CanContinue() && await wireBudget.PrepareReadAsync(ct).ConfigureAwait(false),
            bytes => jobs.Progress(job.Id, cached.Identity.Generation, bytes), wireBudget.Token,
            CanContinue, native.ActiveSettings?.ChunkMb ?? 64).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && (wireBudget.Exceeded || jobs.WireBudgetBlocked) && !ct.IsCancellationRequested)
        {
            throw new PrefetchDeferredException(wireBudget.AccountingFailed || jobs.WireBudgetBlocked
                ? "Warming accounting failed. Repair local metadata storage and restart; playback remains available."
                : "Daily provider-payload warming budget exhausted; verified coverage retained.");
        }
    }
    public static Task WarmAsync(NativeCacheStore store, NativeCachedStream stream, long start, long length,
        Func<long, bool> spend, Action<long> progress, CancellationToken ct, int chunkMb = 64)
        => WarmAsync(store, stream, start, length, bytes => new ValueTask<bool>(spend(bytes)),
            progress, ct, chunkMb: chunkMb);

    public static async Task WarmAsync(NativeCacheStore store, NativeCachedStream stream, long start, long length,
        Func<long, ValueTask<bool>> spend, Action<long> progress, CancellationToken ct,
        Func<bool>? canContinue = null, int chunkMb = 64)
    {
        if (start < 0 || start >= stream.Length || length < 0 || length > stream.Length - start)
            throw new ArgumentException("The warm range is outside the media file.");
        if (chunkMb is < 4 or > 256 || chunkMb % 4 != 0)
            throw new ArgumentOutOfRangeException(nameof(chunkMb));
        var end = length == 0 ? stream.Length : start + length;
        var position = start / NativeCacheStore.BlockSize * NativeCacheStore.BlockSize;
        var alignedEnd = end % NativeCacheStore.BlockSize == 0 ? end
            : end + Math.Min(NativeCacheStore.BlockSize - end % NativeCacheStore.BlockSize, stream.Length - end);
        if (canContinue?.Invoke() == false) throw new PrefetchDeferredException("Warming is paused or foreground playback has priority.");
        var initialMissing = await store.GetMissingRangeBytesAsync(stream.Identity, position, alignedEnd, ct).ConfigureAwait(false);
        await store.RestartPartialWarmAsync(stream.Identity, initialMissing, ct).ConfigureAwait(false);
        await VerifyExistingRangesAsync(store, stream, position, alignedEnd, canContinue, ct).ConfigureAwait(false);
        var missing = await store.GetMissingRangeBytesAsync(stream.Identity, position, alignedEnd, ct).ConfigureAwait(false);
        if (missing == 0)
        {
            if (!stream.IsSourceCurrent) throw new PrefetchDeferredException("Source changed before completion.");
            progress(await store.GetCoverageAsync(stream.Identity, ct).ConfigureAwait(false));
            return;
        }
        var probe = new byte[1];
        while (position < end)
        {
            var chunkEnd = Math.Min(alignedEnd, position + chunkMb * 1024L * 1024L);
            var chunkMissing = await store.GetMissingRangeBytesAsync(stream.Identity, position, chunkEnd, ct).ConfigureAwait(false);
            if (chunkMissing == 0) { position = chunkEnd; continue; }
            using (var reservation = await store.ReserveWarmAsync(stream.Identity, chunkMissing, ct).ConfigureAwait(false)
                ?? throw new PrefetchDeferredException("No writable folder has enough unreserved capacity for this warming chunk."))
            {
                while (position < chunkEnd)
                {
                    position = await store.FindNextMissingOffsetAsync(stream.Identity, position, chunkEnd, ct).ConfigureAwait(false);
                    if (position >= chunkEnd) break;
                    ct.ThrowIfCancellationRequested();
                    if (!stream.IsSourceCurrent) throw new PrefetchDeferredException("Source changed; verified coverage must be rechecked.");
                    var count = Math.Min(NativeCacheStore.BlockSize, stream.Length - position);
                    if (!await spend(count).ConfigureAwait(false)) throw new PrefetchDeferredException("Daily warming budget exhausted or foreground playback has priority.");
                    stream.Position = position;
                    if (await stream.ReadAsync(probe, ct).ConfigureAwait(false) != 1 || !stream.LastReadCacheable || !stream.IsSourceCurrent
                        || await store.FindNextMissingOffsetAsync(stream.Identity, position, Math.Min(position + count, chunkEnd), ct).ConfigureAwait(false) == position)
                        throw new PrefetchDeferredException("Source bytes were not verified or the cache could not commit this range.", countsAsFailure: true);
                    progress(await store.GetCoverageAsync(stream.Identity, ct).ConfigureAwait(false));
                    position = Math.Min(position + count, chunkEnd);
                }
            }
        }
        if (!stream.IsSourceCurrent) throw new PrefetchDeferredException("Source changed before completion.");
        progress(await store.GetCoverageAsync(stream.Identity, ct).ConfigureAwait(false));
    }

    private static async Task VerifyExistingRangesAsync(NativeCacheStore store, NativeCachedStream stream,
        long start, long end, Func<bool>? canContinue, CancellationToken ct)
    {
        // Catalogue coverage is a snapshot. It cannot prove the mounted data still
        // exists or matches its committed hash. Validate only this job's blocks,
        // with bounded catalogue pages and the stream's existing buffer admission.
        var after = start - 1;
        while (true)
        {
            var ranges = await store.ListVerifiedRangesAsync(stream.Identity.Key, after, 100, ct).ConfigureAwait(false);
            if (ranges.Count == 0) return;
            foreach (var range in ranges)
            {
                if (range.Offset >= end) return;
                ct.ThrowIfCancellationRequested();
                if (!stream.IsSourceCurrent) throw new PrefetchDeferredException("Source changed before cache verification.");
                if (canContinue?.Invoke() == false)
                    throw new PrefetchDeferredException("Warming is paused or foreground playback has priority.");
                if (!await stream.VerifyCachedBlockAsync(range.Offset, ct).ConfigureAwait(false))
                {
                    // Missing/truncated/corrupt data invalidates the indexed block
                    // and is filled by the ordinary budgeted path below. An offline
                    // or inaccessible volume leaves coverage intact; defer it.
                    if (!stream.IsSourceCurrent || await store.FindNextMissingOffsetAsync(stream.Identity,
                        range.Offset, range.Offset + range.Count, ct).ConfigureAwait(false) != range.Offset)
                        throw new PrefetchDeferredException("Cached storage is unavailable; completion could not be verified.");
                }
                after = range.Offset;
            }
        }
    }
}
