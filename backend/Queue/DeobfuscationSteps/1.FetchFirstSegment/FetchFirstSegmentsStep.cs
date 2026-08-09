// ReSharper disable InconsistentNaming

using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue;
using Serilog;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Queue.DeobfuscationSteps._1.FetchFirstSegment;

public static class FetchFirstSegmentsStep
{
    public static async Task<List<NzbFileWithFirstSegment>> FetchFirstSegments
    (
        List<NzbFile> nzbFiles,
        INntpClient usenetClient,
        ConfigManager configManager,
        CancellationToken cancellationToken,
        IProgress<int>? progress = null
    )
    {
        var files = nzbFiles.Where(x => x.Segments.Count > 0).ToList();

        if (configManager.IsPipeliningEnabled())
            return await FetchFirstSegmentsPipelined(
                files, usenetClient, configManager, cancellationToken, progress).ConfigureAwait(false);

        return await FetchFirstSegmentsConcurrent(
            files, usenetClient, configManager, cancellationToken, progress).ConfigureAwait(false);
    }

    private static async Task<List<NzbFileWithFirstSegment>> FetchFirstSegmentsConcurrent
    (
        List<NzbFile> files,
        INntpClient usenetClient,
        ConfigManager configManager,
        CancellationToken cancellationToken,
        IProgress<int>? progress
    )
    {
        // Preserve QueueDownloadContext so primary preference / fan-out survive abort linking.
        using var abortCts = ContextualCancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var results = new List<NzbFileWithFirstSegment>(files.Count);
        var completed = 0;

        await foreach (var result in files
                           .Select(x => FetchFirstSegment(x, usenetClient, abortCts.Token))
                           .WithConcurrencyAsync(
                               QueueFanOut.GetConcurrency(configManager, abortCts.Token),
                               abortCts.Token)
                           .WithCancellation(abortCts.Token)
                           .ConfigureAwait(false))
        {
            results.Add(result);
            progress?.Report(++completed);

            if (result.MissingFirstSegment && DeadNzbFailFast.IsImportantNzbFile(result.NzbFile))
            {
                Log.Warning("First segment for `{FileName}` missing across all providers",
                    result.NzbFile.GetSubjectFileName());
                AbortRemainingFirstSegmentChecks(result.NzbFile, abortCts.Cancel);
            }
        }

        return results;
    }

    private static async Task<List<NzbFileWithFirstSegment>> FetchFirstSegmentsPipelined
    (
        List<NzbFile> files,
        INntpClient usenetClient,
        ConfigManager configManager,
        CancellationToken cancellationToken,
        IProgress<int>? progress
    )
    {
        // Import fetches don't benefit from deep BODY windows; cap to bound
        // peak decoded-article memory at boot (~750 KB × depth per connection).
        var depth = Math.Min(configManager.GetPipeliningDepth(), 16);
        var segmentIds = files.Select(x => x.Segments[0].MessageId).ToList();
        var results = new NzbFileWithFirstSegment?[files.Count];
        var indexBySegmentId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < files.Count; i++)
        {
            var key = NntpClient.NormalizeSegmentId(files[i].Segments[0].MessageId);
            if (key.Length > 0)
                indexBySegmentId.TryAdd(key, i);
        }
        var completed = 0;
        NzbFile? abortFile = null;
        using var abortCts = ContextualCancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            await foreach (var article in usenetClient.DecodedArticlesPipelinedAsync(segmentIds, depth, abortCts.Token)
                               .WithCancellation(abortCts.Token).ConfigureAwait(false))
            {
                var key = NntpClient.NormalizeSegmentId(article.SegmentId);
                if (key.Length == 0 || !indexBySegmentId.TryGetValue(key, out var i))
                {
                    Log.Warning(
                        "Pipelined first-segment result SegmentId {SegmentId} did not match any pending file; leaving for rescue",
                        article.SegmentId);
                    if (article.Stream != null)
                    {
                        try { await article.Stream.DisposeAsync().ConfigureAwait(false); }
                        catch (Exception e) { Log.Debug(e, "Failed to dispose unmatched first-segment stream"); }
                    }
                    continue;
                }

                if (article.Found && article.Stream != null)
                {
                    results[i] = await BuildFirstSegment(files[i], article.Stream, article.ArticleHeaders, abortCts.Token)
                        .ConfigureAwait(false);
                    progress?.Report(++completed);
                }
                else if (article.DefinitivelyMissing)
                {
                    // The batch failover already tried every provider; a rescue pass would
                    // just repeat the same misses. Record and move on — unless this is an
                    // important file, in which case remaining checks cannot help.
                    Log.Warning("First segment for `{FileName}` missing across all providers",
                        files[i].GetSubjectFileName());
                    results[i] = BuildMissingFirstSegment(files[i]);
                    progress?.Report(++completed);

                    if (DeadNzbFailFast.IsImportantNzbFile(files[i]))
                    {
                        abortFile = files[i];
                        await abortCts.CancelAsync().ConfigureAwait(false);
                        break;
                    }
                }
                else if (article.Stream != null)
                {
                    try { await article.Stream.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception e) { Log.Debug(e, "Failed to dispose missing first-segment stream"); }
                }
            }
        }
        catch (Exception e) when (e.IsCancellationException() && abortFile is not null)
        {
            // Expected when cancelling the pipeline after an important miss.
        }
        catch (Exception e) when (!e.IsCancellationException())
        {
            Log.Debug($"Pipelined first-segment fetch aborted early ({e.Message}); " +
                      "falling back to per-article failover for the remainder.");
        }

        if (abortFile is not null)
            AbortRemainingFirstSegmentChecks(abortFile, cancel: null);

        var pending = Enumerable.Range(0, files.Count).Where(i => results[i] is null).ToList();
        if (pending.Count > 0)
        {
            using var rescueAbortCts = ContextualCancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await foreach (var (i, result) in pending
                               .Select(i => RescueFirstSegment(i, files[i], usenetClient, rescueAbortCts.Token))
                               .WithConcurrencyAsync(
                                   QueueFanOut.GetConcurrency(configManager, rescueAbortCts.Token),
                                   rescueAbortCts.Token)
                               .WithCancellation(rescueAbortCts.Token)
                               .ConfigureAwait(false))
            {
                results[i] = result;
                progress?.Report(++completed);

                if (result.MissingFirstSegment && DeadNzbFailFast.IsImportantNzbFile(result.NzbFile))
                    AbortRemainingFirstSegmentChecks(result.NzbFile, rescueAbortCts.Cancel);
            }
        }

        return results.Select(x => x!).ToList();
    }

    private static void AbortRemainingFirstSegmentChecks(
        NzbFile nzbFile,
        Action? cancel)
    {
        Log.Warning(
            "Aborting remaining first-segment checks after missing important file `{FileName}`",
            nzbFile.GetSubjectFileName());
        cancel?.Invoke();
        DeadNzbFailFast.FailMissingImportantFile(nzbFile);
    }

    private static async Task<(int index, NzbFileWithFirstSegment result)> RescueFirstSegment
    (
        int index,
        NzbFile nzbFile,
        INntpClient usenetClient,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return (index, await FetchFirstSegment(nzbFile, usenetClient, cancellationToken).ConfigureAwait(false));
        }
        catch (UsenetArticleNotFoundException)
        {
            Log.Warning("First segment for `{FileName}` missing across all providers",
                nzbFile.GetSubjectFileName());
            return (index, BuildMissingFirstSegment(nzbFile));
        }
#pragma warning disable CA2016 // CA2016: classify cancellation regardless of the ambient token -- forwarding it would misclassify cancellations from internal timeout/child tokens
        catch (Exception e) when (!e.IsCancellationException())
#pragma warning restore CA2016
        {
            // Transient provider errors must not be treated as permanent missing segments
            // (otherwise fail-fast would mark good NZBs failed — see nzbdav-dev#245).
            e.LogWarningKnownOrStack(
                "First segment for `{FileName}` unavailable due to provider error; not treating as permanently missing",
                nzbFile.GetSubjectFileName());
            throw;
        }
    }

    private static NzbFileWithFirstSegment BuildMissingFirstSegment(NzbFile nzbFile) => new()
    {
        NzbFile = nzbFile,
        First16KB = null,
        Header = null,
        MissingFirstSegment = true,
        ReleaseDate = DateTimeOffset.UtcNow,
    };

    private static async Task<NzbFileWithFirstSegment> BuildFirstSegment
    (
        NzbFile nzbFile,
        YencStream bodyStream,
        UsenetArticleHeader? articleHeaders,
        CancellationToken cancellationToken
    )
    {
        await using var stream = bodyStream;
        var totalRead = 0;
        var buffer = new byte[16 * 1024];
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) break;
            totalRead += read;
        }

        var first16KB = totalRead < buffer.Length ? buffer.AsSpan(0, totalRead).ToArray() : buffer;
        var yencHeaders = await stream.GetYencHeadersAsync(cancellationToken).ConfigureAwait(false);
        if (yencHeaders is not null)
            nzbFile.Segments[0].ByteRange =
                LongRange.FromStartAndSize(yencHeaders.PartOffset, yencHeaders.PartSize);

        return new NzbFileWithFirstSegment
        {
            NzbFile = nzbFile,
            First16KB = first16KB,
            Header = yencHeaders,
            MissingFirstSegment = false,
            ReleaseDate = articleHeaders?.Date ?? DateTimeOffset.UtcNow,
        };
    }

    private static async Task<NzbFileWithFirstSegment> FetchFirstSegment
    (
        NzbFile nzbFile,
        INntpClient usenetClient,
        CancellationToken cancellationToken
    )
    {
        try
        {
            // get the first article stream
            var firstSegment = nzbFile.Segments[0].MessageId;
            var article = await usenetClient.DecodedArticleAsync(firstSegment, cancellationToken).ConfigureAwait(false);
            await using var bodyStream = article.Stream!;

            // read up to the first 16KB from the stream
            var totalRead = 0;
            var buffer = new byte[16 * 1024];
            while (totalRead < buffer.Length)
            {
                var read = await bodyStream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                totalRead += read;
            }

            // determine bytes read
            var first16KB = totalRead < buffer.Length
                ? buffer.AsSpan(0, totalRead).ToArray()
                : buffer;

            // get the yencHeaders
            var yencHeaders = await bodyStream
                .GetYencHeadersAsync(cancellationToken)
                .ConfigureAwait(false);
            if (yencHeaders is not null)
                nzbFile.Segments[0].ByteRange =
                    LongRange.FromStartAndSize(yencHeaders.PartOffset, yencHeaders.PartSize);

            // return
            return new NzbFileWithFirstSegment
            {
                NzbFile = nzbFile,
                First16KB = first16KB,
                Header = yencHeaders,
                MissingFirstSegment = false,
                ReleaseDate = article.ArticleHeaders!.Date
            };
        }
        catch (UsenetArticleNotFoundException)
        {
            return BuildMissingFirstSegment(nzbFile);
        }
        catch (Exception e) when (
#pragma warning disable CA2016 // CA2016: classify cancellation regardless of the ambient token -- forwarding it would misclassify cancellations from internal timeout/child tokens
            e.IsTransientTransportException() && !e.IsCancellationException())
#pragma warning restore CA2016
        {
            throw new RetryableDownloadException(
                $"Transient provider error fetching first segment of " +
                $"`{nzbFile.GetSubjectFileName()}`: {e.Message}", e);
        }
    }

    public class NzbFileWithFirstSegment
    {
        private static readonly byte[] Rar4Magic = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00];
        private static readonly byte[] Rar5Magic = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00];

        public required NzbFile NzbFile { get; init; }
        public required UsenetYencHeader? Header { get; init; }
        public required byte[]? First16KB { get; init; }
        public required bool MissingFirstSegment { get; init; }
        public required DateTimeOffset ReleaseDate { get; init; }

        public bool HasRar4Magic() => HasMagic(Rar4Magic);
        public bool HasRar5Magic() => HasMagic(Rar5Magic);

        private bool HasMagic(byte[] sequence)
        {
            return First16KB?.Length >= sequence.Length &&
                   First16KB.AsSpan(0, sequence.Length).SequenceEqual(sequence);
        }
    }
}
