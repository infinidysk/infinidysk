using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using NWebDav.Server.Helpers;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services;
using NzbWebDAV.Streams;
using NzbWebDAV.Utils;
using Serilog;
using Serilog.Events;

namespace NzbWebDAV.Middlewares;

public class ExceptionMiddleware(RequestDelegate next, ConfigManager configManager, StreamingFailureTracker failureTracker)
{
    private static readonly ConcurrentDictionary<string, (DateTime LastLogged, int SuppressedCount)> RecentMissingArticles = new();
    private static readonly ConcurrentDictionary<string, (DateTime LastLogged, int SuppressedCount)> RecentConnectionLimitErrors = new();
    private static readonly ConcurrentDictionary<string, (DateTime LastLogged, int SuppressedCount)> RecentSeekErrors = new();
    private static readonly ConcurrentDictionary<string, (DateTime LastLogged, int SuppressedCount)> RecentReadErrors = new();
    private static readonly ConcurrentDictionary<string, (DateTime LastLogged, int SuppressedCount)> RecentStreamingReadTimeouts = new();
    private static readonly ConcurrentDictionary<Guid, DateTime> RecentRepairTriggers = new();
    private static readonly TimeSpan DedupeWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RepairDedupeWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CleanupThreshold = TimeSpan.FromMinutes(5);
    private static int _callCount;

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception e) when (IsCausedByAbortedRequest(e, context))
        {
            // If the response has not started, we can write our custom response
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = 499; // Non-standard status code for client closed request
                await context.Response.WriteAsync("Client closed request.").ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e.TryGetCausingException(out UsenetArticleNotFoundException? notFound))
        {
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = 404;
            }

            var filePath = GetRequestFilePath(context);
            var dedupeKey = $"{filePath}|{notFound!.SegmentId}";
            LogWithDedup(RecentMissingArticles, dedupeKey, suppressed =>
            {
                if (suppressed > 0)
                    Log.Error(
                        "File {FilePath} has missing articles: {Reason} (suppressed {SuppressedCount} duplicates in last 60s)",
                        filePath,
                        notFound.Message,
                        suppressed);
                else
                    Log.Error(
                        "File {FilePath} has missing articles: {Reason}",
                        filePath,
                        notFound.Message);
            });

            if (context.Items["DavItem"] is DavItem davItem)
            {
                RecordMissingArticleForFailFast(davItem, notFound.SegmentId);
                ScheduleRepair(davItem.Id);
            }

            AbortStartedResponse(context);
        }
        catch (SeekPositionNotFoundException)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = 404;
            }

            var filePath = GetRequestFilePath(context);
            var seekPosition = context.Request.GetRange()?.Start?.ToString() ?? "unknown";
            var dedupeKey = $"{filePath}|{seekPosition}";
            LogWithDedup(RecentSeekErrors, dedupeKey, suppressed =>
            {
                if (suppressed > 0)
                    Log.Error(
                        "File {FilePath} could not seek to byte position {SeekPosition} (suppressed {SuppressedCount} duplicates in last 60s)",
                        filePath,
                        seekPosition,
                        suppressed);
                else
                    Log.Error(
                        "File {FilePath} could not seek to byte position {SeekPosition}",
                        filePath,
                        seekPosition);
            });

            AbortStartedResponse(context);
        }
        catch (CouldNotLoginToUsenetException e)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = 503;
            }

            var filePath = GetRequestFilePath(context);
            var errorDetail = e.InnerException?.Message ?? e.Message;
            if (errorDetail.Contains("connection limit", StringComparison.OrdinalIgnoreCase))
            {
                LogWithDedup(RecentConnectionLimitErrors, errorDetail, suppressed =>
                {
                    if (suppressed > 0)
                        Log.Warning(
                            "Provider connection limit reached: {ErrorMessage} (suppressed {SuppressedCount} duplicates in last 60s)",
                            errorDetail,
                            suppressed);
                    else
                        Log.Warning("Provider connection limit reached: {ErrorMessage}", errorDetail);
                });
            }
            else
            {
                Log.Error("File {FilePath} provider authentication failed: {ErrorMessage}", filePath, errorDetail);
            }

            AbortStartedResponse(context);
        }
        catch (CouldNotConnectToUsenetException e)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = 503;
            }

            var filePath = GetRequestFilePath(context);
            Log.Error("File {FilePath} could not connect to usenet provider: {ErrorMessage}", filePath, e.Message);
            AbortStartedResponse(context);
        }
        catch (Exception e) when (e.TryGetCausingException(out StreamingReadTimeoutException? _))
        {
            // Backend-wait deadline (not client disconnect). Fail fast before headers so
            // rclone/FUSE can surface an HTTP error instead of wedging in D-state; after
            // headers we can only abort the truncated body.
            var filePath = GetRequestFilePath(context);
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.Headers.RetryAfter = "5";
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync(
                    "Usenet backend did not deliver within the streaming-read-timeout. Retry shortly.",
                    context.RequestAborted).ConfigureAwait(false);
                LogWithDedup(RecentStreamingReadTimeouts, filePath, suppressed =>
                {
                    if (suppressed > 0)
                        Log.Warning(
                            "WebDAV read failed fast. Path={Path} Reason: {Reason} (suppressed {SuppressedCount} duplicates in last 60s)",
                            filePath,
                            "streaming-read-timeout",
                            suppressed);
                    else
                        Log.Warning(
                            "WebDAV read failed fast. Path={Path} Reason: {Reason}",
                            filePath,
                            "streaming-read-timeout");
                });
                Log.Debug(e, "WebDAV streaming-read-timeout stack");
                return;
            }

            AbortStartedResponse(context);
            LogWithDedup(RecentStreamingReadTimeouts, filePath + "|after-headers", suppressed =>
            {
                if (suppressed > 0)
                    Log.Warning(
                        "WebDAV read aborted after headers due to backend deadline. Path={Path} Reason: {Reason} (suppressed {SuppressedCount} duplicates in last 60s)",
                        filePath,
                        "streaming-read-timeout-after-headers",
                        suppressed);
                else
                    Log.Warning(
                        "WebDAV read aborted after headers due to backend deadline. Path={Path} Reason: {Reason}",
                        filePath,
                        "streaming-read-timeout-after-headers");
            });
            Log.Debug(e, "WebDAV streaming-read-timeout after-headers stack");
        }
        catch (Exception e) when (
            IsDavItemRequest(context) &&
            e.TryGetCausingException(out CorruptRarException? corruptRar))
        {
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = 404;
            }

            var filePath = GetRequestFilePath(context);
            var seekPosition = context.Request.GetRange()?.Start?.ToString() ?? "0";
            var reason = corruptRar!.Message;
            var dedupeKey = $"{filePath}|{seekPosition}|{reason}";
            LogWithDedup(RecentReadErrors, dedupeKey, suppressed =>
            {
                if (suppressed > 0)
                    Log.Error(
                        "File {FilePath} contains a corrupt RAR at byte position {SeekPosition}: {Reason} (suppressed {SuppressedCount} duplicates in last 60s)",
                        filePath,
                        seekPosition,
                        reason,
                        suppressed);
                else
                    Log.Error(
                        "File {FilePath} contains a corrupt RAR at byte position {SeekPosition}: {Reason}",
                        filePath,
                        seekPosition,
                        reason);
            });

            if (context.Items["DavItem"] is DavItem davItem)
                ScheduleRepair(davItem.Id);

            AbortStartedResponse(context);
        }
        catch (Exception e) when (IsDavItemRequest(context))
        {
            // A volume that is short or unresolvable is missing data, not a server
            // fault, and repairing the item is what can actually fix it.
            var isIncompleteData = e.TryGetCausingException(out IncompleteMultipartPartException? _);
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = isIncompleteData ? 404 : 500;
            }

            var filePath = GetRequestFilePath(context);
            var seekPosition = context.Request.GetRange()?.Start?.ToString() ?? "0";
            var userAgent = context.Request.Headers.UserAgent.ToString();
            if (string.IsNullOrWhiteSpace(userAgent))
                userAgent = "unknown";

            // Known download errors carry a human-readable message;
            // reserve full stack traces for unexpected failures.
            var isKnown = IsKnownDownloadException(e, out var knownError);
            var reason = isKnown ? knownError : e.GetType().Name;
            // Data that was never posted completely is a repairable state of the item,
            // not a fault in serving it, so it warns rather than errors.
            var knownLevel = isIncompleteData ? LogEventLevel.Warning : LogEventLevel.Error;
            var dedupeKey = $"{filePath}|{seekPosition}|{reason}";
            LogWithDedup(RecentReadErrors, dedupeKey, suppressed =>
            {
                if (isKnown)
                {
                    if (suppressed > 0)
                        Log.Write(
                            knownLevel,
                            "File {FilePath} could not be read from byte position {SeekPosition}: {Reason} (client {UserAgent}, suppressed {SuppressedCount} duplicates in last 60s)",
                            filePath,
                            seekPosition,
                            knownError,
                            userAgent,
                            suppressed);
                    else
                        Log.Write(
                            knownLevel,
                            "File {FilePath} could not be read from byte position {SeekPosition}: {Reason} (client {UserAgent})",
                            filePath,
                            seekPosition,
                            knownError,
                            userAgent);
                }
                else if (suppressed > 0)
                {
                    Log.Error(
                        e,
                        "File {FilePath} could not be read from byte position {SeekPosition} (client {UserAgent}, suppressed {SuppressedCount} duplicates in last 60s)",
                        filePath,
                        seekPosition,
                        userAgent,
                        suppressed);
                }
                else
                {
                    Log.Error(
                        e,
                        "File {FilePath} could not be read from byte position {SeekPosition} (client {UserAgent})",
                        filePath,
                        seekPosition,
                        userAgent);
                }
            });

            if ((IsTruncatedCiphertextException(e) || isIncompleteData) &&
                context.Items["DavItem"] is DavItem truncatedItem)
            {
                ScheduleRepair(truncatedItem.Id);
            }

            AbortStartedResponse(context);
        }
    }

    /// <summary>
    /// Streaming is the only check that reaches freshly imported (history-linked) items, so a
    /// missing article discovered mid-stream must feed the step-0 queue precheck. Otherwise a
    /// re-grab of the same broken release imports cleanly again and loops through repair
    /// forever (issue #732). Failing the re-grab pre-import lets Arr blocklist it properly.
    /// </summary>
    internal static void RecordMissingArticleForFailFast(DavItem davItem, string segmentId)
    {
        if (!FilenameUtil.IsImportantFileType(davItem.Name))
            return;
        HealthCheckService.AddMissingSegmentIds([segmentId]);
    }

    private static void AbortStartedResponse(HttpContext context)
    {
        if (context.Response.HasStarted)
            context.Abort();
    }

    private void ScheduleRepair(Guid davItemId)
    {
        if (!configManager.IsRepairJobEnabled())
            return;

        // Count every distinct streaming failure before applying either threshold or deduplication.
        // Repeated failures must still advance the repair threshold while duplicate DB scheduling
        // writes remain suppressed below.
        var failureCount = failureTracker.RecordFailure(davItemId);
        var threshold = configManager.GetAutoRemoveAfterFailures();
        if (!ShouldScheduleUrgentRepair(threshold, failureCount))
        {
            Log.Information(
                "Deferring dynamic repair for DavItem {DavItemId} until streaming failure {FailureCount}/{FailureThreshold}",
                davItemId, failureCount, threshold);
            return;
        }

        var now = DateTime.UtcNow;
        var isDuplicate = false;
        RecentRepairTriggers.AddOrUpdate(
            davItemId,
            _ => now,
            (_, existing) =>
            {
                if (now - existing < RepairDedupeWindow)
                {
                    isDuplicate = true;
                    return existing;
                }
                return now;
            });

        if (isDuplicate)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await using var dbContext = new DavDatabaseContext();
                var item = await dbContext.Items.FindAsync(davItemId).ConfigureAwait(false);
                if (item == null)
                    return;

                // UnixEpoch sorts first in HealthCheckService (non-null before null, then ascending).
                // Only skip if already urgent — overdue items must still be bumped (Pukabyte#4).
                var urgent = DateTimeOffset.UnixEpoch;
                if (item.NextHealthCheck == urgent)
                    return;

                item.NextHealthCheck = urgent;
                await dbContext.SaveChangesAsync().ConfigureAwait(false);
                Log.Information("Scheduled dynamic repair for {FilePath}", item.Path);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to schedule dynamic repair for DavItem {DavItemId}", davItemId);
            }
        });
    }

    internal static bool ShouldScheduleUrgentRepair(int threshold, int failureCount)
    {
        return threshold <= 0 || failureCount >= threshold;
    }

    private static void LogWithDedup(
        ConcurrentDictionary<string, (DateTime LastLogged, int SuppressedCount)> store,
        string key,
        Action<int> logAction)
    {
        var now = DateTime.UtcNow;
        var suppressed = 0;
        var shouldLog = false;

        store.AddOrUpdate(
            key,
            _ =>
            {
                shouldLog = true;
                return (now, 0);
            },
            (_, existing) =>
            {
                if (now - existing.LastLogged < DedupeWindow)
                {
                    suppressed = existing.SuppressedCount + 1;
                    return (existing.LastLogged, suppressed);
                }

                shouldLog = true;
                suppressed = existing.SuppressedCount;
                return (now, 0);
            });

        if (shouldLog)
            logAction(suppressed);

        CleanupStaleEntries();
    }

    private static void CleanupStaleEntries()
    {
        if (Interlocked.Increment(ref _callCount) % 100 != 0)
            return;

        var cutoff = DateTime.UtcNow - CleanupThreshold;
        foreach (var kvp in RecentMissingArticles)
        {
            if (kvp.Value.LastLogged < cutoff)
                RecentMissingArticles.TryRemove(kvp.Key, out _);
        }
        foreach (var kvp in RecentConnectionLimitErrors)
        {
            if (kvp.Value.LastLogged < cutoff)
                RecentConnectionLimitErrors.TryRemove(kvp.Key, out _);
        }
        foreach (var kvp in RecentSeekErrors)
        {
            if (kvp.Value.LastLogged < cutoff)
                RecentSeekErrors.TryRemove(kvp.Key, out _);
        }
        foreach (var kvp in RecentReadErrors)
        {
            if (kvp.Value.LastLogged < cutoff)
                RecentReadErrors.TryRemove(kvp.Key, out _);
        }
        foreach (var kvp in RecentStreamingReadTimeouts)
        {
            if (kvp.Value.LastLogged < cutoff)
                RecentStreamingReadTimeouts.TryRemove(kvp.Key, out _);
        }
        foreach (var kvp in RecentRepairTriggers)
        {
            if (kvp.Value < cutoff)
                RecentRepairTriggers.TryRemove(kvp.Key, out _);
        }
    }

    private static bool IsKnownDownloadException(Exception e, out string message)
    {
        // Walk the chain so wrappers (e.g. AggregateException / Task) still
        // match queue-side helpers — including bare InvalidFormatException.
        for (var current = e; current != null; current = current.InnerException)
        {
            if (current is EndOfStreamException &&
                current.Message.StartsWith(
                    AesDecoderStream.TruncatedCiphertextMessagePrefix,
                    StringComparison.Ordinal))
            {
                message = $"Encrypted file data ended prematurely. {current.Message}";
                return true;
            }

            if (current.IsRetryableDownloadException() || current.IsNonRetryableDownloadException())
            {
                message = current.Message;
                return true;
            }
        }

        message = string.Empty;
        return false;
    }

    private static bool IsTruncatedCiphertextException(Exception e)
    {
        for (var current = e; current != null; current = current.InnerException)
        {
            if (current is EndOfStreamException &&
                current.Message.StartsWith(
                    AesDecoderStream.TruncatedCiphertextMessagePrefix,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsCausedByAbortedRequest(Exception e, HttpContext context)
    {
        var isAffectedException = e is OperationCanceledException or EndOfStreamException;
        var isRequestAborted = context.RequestAborted.IsCancellationRequested ||
                               SigtermUtil.GetCancellationToken().IsCancellationRequested;
        return isAffectedException && isRequestAborted;
    }

    private static string GetRequestFilePath(HttpContext context)
    {
        return context.Items["DavItem"] is DavItem davItem
            ? davItem.Path
            : context.Request.Path;
    }

    private static bool IsDavItemRequest(HttpContext context)
    {
        return context.Items["DavItem"] is DavItem;
    }
}
