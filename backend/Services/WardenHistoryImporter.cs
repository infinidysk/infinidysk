using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Mints Warden fingerprints from imports that already failed because their articles are gone.
/// Until now Warden only learned from Watchtower and playback, so an install that grabs through
/// Sonarr/Radarr filtered nothing: the same dead release could be grabbed again and again. Every
/// failed queue item keeps its nzb, which carries the size, poster and post date a fingerprint
/// needs, so that history is a ready-made source of verdicts.
/// </summary>
public class WardenHistoryImporter(
    IDbContextFactory<DavDatabaseContext> dbContextFactory,
    IBlobStore blobStore,
    WardenStore warden
)
{
    /// <summary>
    /// Failures that mean "the articles are not on the servers". Everything else a queue item can
    /// fail with (unparsable rar headers, unsupported compression, no media files, encrypted
    /// archives, CRC mismatches) means the data arrived but could not be used, which says nothing
    /// about the release being dead. Marking those would hide healthy releases from every future
    /// search, so they are deliberately excluded.
    /// </summary>
    private static readonly string[] DeadFailureMarkers =
    [
        "missing segments across all providers",
        "Missing rar volumes detected.",
    ];

    public sealed record Result
    {
        public required int Scanned { get; init; }
        public required int Eligible { get; init; }
        public required int Fingerprinted { get; init; }
        public required int Distinct { get; init; }
        public required int Added { get; init; }
        public required int SkippedMissingNzb { get; init; }
        public required int SkippedUnparsableNzb { get; init; }
        public required int SkippedNoFingerprint { get; init; }
        public required bool DryRun { get; init; }
    }

    private int _running;

    /// <summary>
    /// Scans failed history and returns what was (or would be) added. Returns <c>null</c> when a
    /// scan is already running: a second pass re-reads every blob to reach the same verdicts, so
    /// an impatient second click should be told to wait rather than doubling the work.
    /// </summary>
    public async Task<Result?> ImportAsync(bool dryRun, CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return null;
        try
        {
            return await RunAsync(dryRun, ct).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<Result> RunAsync(bool dryRun, CancellationToken ct)
    {
        await using var ctx = await dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var scanned = 0;
        var eligible = 0;
        var missingNzb = 0;
        var unparsable = 0;
        var noFingerprint = 0;
        var fingerprints = new List<string>();

        var failed = ctx.HistoryItems
            .AsNoTracking()
            .Where(x => x.DownloadStatus == HistoryItem.DownloadStatusOption.Failed && x.FailMessage != null)
            .OrderBy(x => x.CreatedAt)
            .AsAsyncEnumerable();

        await foreach (var item in failed.WithCancellation(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            scanned++;
            if (!IsDeadFailure(item.FailMessage)) continue;
            eligible++;

            if (item.NzbBlobId is not { } blobId)
            {
                missingNzb++;
                continue;
            }

            var fingerprint = await TryFingerprintAsync(blobId, item.TotalSegmentBytes, ct).ConfigureAwait(false);
            switch (fingerprint)
            {
                case FingerprintOutcome.MissingBlob:
                    missingNzb++;
                    break;
                case FingerprintOutcome.Unparsable:
                    unparsable++;
                    break;
                case FingerprintOutcome.NotEnoughMetadata:
                    noFingerprint++;
                    break;
                case FingerprintOutcome.Ok ok:
                    fingerprints.Add(ok.Fingerprint);
                    break;
            }
        }

        var distinct = fingerprints.Distinct(StringComparer.Ordinal).ToList();
        var added = 0;
        if (!dryRun && distinct.Count > 0)
        {
            var before = warden.LocalCount;
            warden.MarkDeadMany(distinct);
            added = Math.Max(0, warden.LocalCount - before);
        }

        Log.Information(
            "Warden: scanned {Scanned:n0} failed imports, {Eligible:n0} dead-article failures, " +
            "{Distinct:n0} distinct fingerprints, {Added:n0} added{DryRun}",
            scanned, eligible, distinct.Count, added, dryRun ? " (dry run)" : "");

        return new Result
        {
            Scanned = scanned,
            Eligible = eligible,
            Fingerprinted = fingerprints.Count,
            Distinct = distinct.Count,
            Added = added,
            SkippedMissingNzb = missingNzb,
            SkippedUnparsableNzb = unparsable,
            SkippedNoFingerprint = noFingerprint,
            DryRun = dryRun,
        };
    }

    internal static bool IsDeadFailure(string? failMessage) =>
        failMessage is not null
        && DeadFailureMarkers.Any(marker => failMessage.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private abstract record FingerprintOutcome
    {
        public sealed record Ok(string Fingerprint) : FingerprintOutcome;
        public sealed record MissingBlob : FingerprintOutcome;
        public sealed record Unparsable : FingerprintOutcome;
        public sealed record NotEnoughMetadata : FingerprintOutcome;
    }

    private async Task<FingerprintOutcome> TryFingerprintAsync(Guid blobId, long totalBytes, CancellationToken ct)
    {
        Stream? stream;
        try
        {
            stream = blobStore.ReadBlob(blobId);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Debug(e, "Warden: could not read nzb blob {BlobId}", blobId);
            return new FingerprintOutcome.MissingBlob();
        }

        if (stream is null) return new FingerprintOutcome.MissingBlob();

        NzbDocument nzb;
        await using (stream)
        {
            try
            {
                nzb = await NzbDocument.LoadAsync(stream, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
            {
                Log.Debug(e, "Warden: could not parse nzb blob {BlobId}", blobId);
                return new FingerprintOutcome.Unparsable();
            }
        }

        var fingerprint = ComputeFingerprint(nzb, totalBytes);
        return fingerprint is null
            ? new FingerprintOutcome.NotEnoughMetadata()
            : new FingerprintOutcome.Ok(fingerprint);
    }

    /// <summary>
    /// Mirrors how a search result is fingerprinted: the release's total size with the poster and
    /// post date of its first file. Falls back to the summed segment bytes when history has no
    /// size recorded, so older rows still fingerprint.
    /// </summary>
    internal static string? ComputeFingerprint(NzbDocument nzb, long totalBytes)
    {
        var first = nzb.Files.FirstOrDefault();
        if (first is null) return null;
        var size = totalBytes > 0
            ? totalBytes
            : nzb.Files.Sum(f => f.Segments.Sum(s => s.Bytes));
        return WardenFingerprint.Compute(size, first.Poster, first.PostedAt);
    }
}
