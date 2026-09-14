using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Api.Errors;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Queue;

/// <summary>
/// Transport-neutral NZB enqueue used by SAB, WebDAV watch folders, and migration.
/// </summary>
public class NzbSubmissionService(
    DavDatabaseClient dbClient,
    QueueManager queueManager,
    ConfigManager configManager,
    WebsocketManager websocketManager)
{
    /// <summary>
    /// Test hook invoked after the duplicate pre-check and before the blob is written,
    /// so the UNIQUE retry path can be exercised without a real concurrent request.
    /// </summary>
    internal Func<Task>? AfterDuplicatePreCheckHook { get; set; }

    public async Task<NzbSubmissionResult> SubmitAsync(NzbSubmissionRequest request)
    {
        await using var sourceStream = request.NzbFileStream;
        var id = request.NzoId ?? Guid.NewGuid();
        var arrDownloadId = request.Origin switch
        {
            NzbSubmissionOrigin.ExternalSabAdd => id,
            NzbSubmissionOrigin.HistoryRetry => request.ArrDownloadId,
            _ => null,
        };
        var category = StringUtil.EmptyToNull(request.Category)
                       ?? configManager.GetManualUploadCategory();

        var replacesExisting = await dbClient.Ctx.QueueItems
            .AnyAsync(
                x => x.FileName == request.FileName && x.Category == category,
                request.CancellationToken)
            .ConfigureAwait(false);

        IDisposable? admissionReservation = null;
        if (!replacesExisting)
        {
            var maxItems = configManager.GetQueueMaxItems();
            if (maxItems > 0)
            {
                var currentCount = await dbClient.Ctx.QueueItems
                    .CountAsync(request.CancellationToken)
                    .ConfigureAwait(false);
                var resumeThreshold = configManager.GetQueueResumeThreshold();
#pragma warning disable CA2000 // reservation is assigned to the method-scoped using below; the only statements between creation and the using are a null check and logging
                admissionReservation = queueManager.TryReserveQueueSlot(
                    currentCount, maxItems, resumeThreshold);
#pragma warning restore CA2000
                if (admissionReservation is null)
                {
                    Log.Warning(
                        "Rejected NZB submission because the queue has {QueueCount} of {QueueLimit} items. " +
                        "Admission resumes at or below {ResumeThreshold} items.",
                        currentCount, maxItems, resumeThreshold);
                    return new NzbSubmissionResult
                    {
                        Status = false,
                        Error = $"Queue is full ({currentCount} of {maxItems} items); " +
                                $"submissions resume at or below {resumeThreshold}.",
                    };
                }
            }
        }

        using var queueSlotReservation = admissionReservation;
        using var submissionIdLease = request.NzoId.HasValue
            ? await queueManager.AcquireSubmissionIdLeaseAsync(request.NzoId.Value, request.CancellationToken)
                .ConfigureAwait(false)
            : null;
        if (request.NzoId.HasValue)
        {
            var nzoId = request.NzoId.Value;
            var collision = await dbClient.Ctx.QueueItems
                .AnyAsync(q => q.Id == nzoId, request.CancellationToken)
                .ConfigureAwait(false);
            if (collision)
            {
                throw new BadHttpRequestException($"Requested queue item ID '{nzoId}' already exists.");
            }

            // The lease serializes assigned-ID recovery with the blob write and
            // final queue commit. With no row owner, an existing payload is an
            // orphan from an interrupted attempt and can be reclaimed safely.
            if (BlobStore.Exists(nzoId))
                BlobStore.Delete(nzoId);
        }

        await HandleExistingQueueItemAsync(
                request.FileName,
                category,
                request.ReplaceExistingQueueItem,
                request.CancellationToken)
            .ConfigureAwait(false);
        if (AfterDuplicatePreCheckHook is not null)
            await AfterDuplicatePreCheckHook().ConfigureAwait(false);

        QueueItem? queueItem;
        Guid[] removedIds = [];
        string? backupPath = null;
        try
        {
            var prepared = await NzbStreamUtil.OpenMaybeCompressedAsync(
                    sourceStream, request.CancellationToken)
                .ConfigureAwait(false);
            await using var nzbInputStream = prepared.Stream;
            try
            {
                // Store normalized XML so every downstream parser remains
                // compression-agnostic. The bounded stream enforces the
                // decompressed size limit during the copy, so an oversize
                // document (or gzip bomb) fails before filling the disk.
                await using var bounded = new LimitedReadStream(
                    nzbInputStream,
                    NzbInputLimits.Default.MaxXmlBytes,
                    NzbInputValidator.CreateSizeLimitException);
                await BlobStore.WriteBlob(id, bounded, request.CancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidDataException exception) when (prepared.IsGzip)
            {
                throw new BadHttpRequestException("The uploaded gzip NZB is invalid.", exception);
            }

            // compute the total segment bytes before any backup, so a rejected
            // document never leaves a backup file behind
            await using var nzbFileStream = BlobStore.ReadBlob(id)!;
            var totalSegmentBytes = NzbInputValidator.ValidateAndSumSegmentBytes(
                nzbFileStream, NzbInputLimits.Default, request.CancellationToken);

            // backup the nzb file if enabled
            if (configManager.IsNzbBackupEnabled())
            {
                var backupLocation = configManager.GetNzbBackupLocation();
                if (backupLocation != null)
                {
                    backupPath = await BackupNzbAsync(
                            id, request.FileName, category, backupLocation, request.CancellationToken)
                        .ConfigureAwait(false);
                }
            }

            // Keep enqueues after any manually moved item in their priority band.
            // CreatedAt remains the immutable enqueue timestamp; SortOrder owns
            // user-directed positioning.
            var createdAt = DateTime.Now;
            var bandMax = await dbClient.Ctx.QueueItems
                .Where(item => item.Priority == request.Priority)
                .Select(item => (long?)item.SortOrder)
                .MaxAsync(request.CancellationToken)
                .ConfigureAwait(false) ?? 0;
            var sortOrder = Math.Max(
                createdAt.Ticks,
                checked(bandMax + QueueItem.SortOrderStride));

            // create the queue item record
            queueItem = new QueueItem
            {
                Id = id,
                CreatedAt = createdAt,
                SortOrder = sortOrder,
                FileName = request.FileName,
                JobName = FilenameUtil.GetJobName(request.FileName),
                NzbFileSize = nzbFileStream.Length,
                TotalSegmentBytes = totalSegmentBytes,
                Category = category,
                Priority = request.Priority,
                PostProcessing = request.PostProcessing,
                PauseUntil = request.PauseUntil,
                IndexerName = request.IndexerName,
                ContentGroupKey = request.ContentGroupKey,
                ArrDownloadId = arrDownloadId,
            };

            // record the original NZB filename so it can be served at download time
            var nzbName = new NzbName
            {
                Id = id,
                FileName = request.FileName
            };

            // save — never Clear() the change tracker here: WebDAV watch-folder create
            // reads the new QueueItem from the tracker after SubmitAsync returns.
            var commitResult = await queueManager.CommitSubmissionAsync(
                queueItem,
                nzbName,
                request.ReplaceExistingQueueItem,
                dbClient,
                request.CancellationToken).ConfigureAwait(false);

            removedIds = commitResult.RemovedIds;
        }
        catch
        {
            // Delete partial or unreferenced blobs after ingest/database failures,
            // plus any backup sidecar written before the failure.
            BlobStore.Delete(id);
            TryDeleteBackupFile(backupPath);
            throw;
        }

        foreach (var removedId in removedIds)
        {
            try
            {
                BlobStore.Delete(removedId);
                _ = websocketManager.SendMessage(WebsocketTopic.QueueItemRemoved, removedId.ToString());
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Log.Warning(exception, "Could not clean up replaced NZB blob {QueueItemId}", removedId);
            }
        }

        _ = DavDatabaseContext.RcloneVfsForget(["/nzbs"], request.CancellationToken);

        // inform the frontend that a new item was added to the queue
        var message = QueueItemAddedPayload.FromQueueItem(queueItem).ToJson();
        _ = websocketManager.SendMessage(WebsocketTopic.QueueItemAdded, message);

        // awaken the queue if it is sleeping
        queueManager.AwakenQueue(request.PauseUntil);

        // return response
        return new NzbSubmissionResult()
        {
            Status = true,
            NzoIds = [queueItem.Id.ToString()],
        };
    }

    private async Task HandleExistingQueueItemAsync(
        string fileName,
        string category,
        bool replaceExisting,
        CancellationToken ct)
    {
        var existing = await dbClient.Ctx.QueueItems.AsNoTracking()
            .AnyAsync(q => q.Category == category && q.FileName == fileName, ct)
            .ConfigureAwait(false);
        if (existing && !replaceExisting)
        {
            throw new BadHttpRequestException(
                $"A queue item named '{fileName}' already exists in category '{category}'.");
        }
    }

    internal static bool IsCategoryFileNameUniqueViolation(DbUpdateException ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (!e.IsUniqueConstraintException()) continue;

            var message = e.Message;
            if (message.Contains("IX_QueueItems_Category_FileName", StringComparison.OrdinalIgnoreCase))
                return true;
            if (message.Contains("QueueItems.Category", StringComparison.OrdinalIgnoreCase)
                && message.Contains("QueueItems.FileName", StringComparison.OrdinalIgnoreCase))
                return true;
            if (message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
                && message.Contains("Category", StringComparison.OrdinalIgnoreCase)
                && message.Contains("FileName", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Copies the committed blob into the configured backup directory and
    /// returns the written path so a later submission failure can remove it.
    /// A failed or cancelled copy deletes its own partial file.
    /// </summary>
    private static async Task<string> BackupNzbAsync(
        Guid id, string fileName, string category, string backupLocation, CancellationToken ct)
    {
        string? destPath = null;
        try
        {
            ValidateBackupCategory(category);
            ValidateBackupLeafName(fileName);
            fileName = Path.GetFileName(fileName);

            var backupRoot = Path.GetFullPath(backupLocation);
            var backupRootPrefix = Path.EndsInDirectorySeparator(backupRoot)
                ? backupRoot
                : backupRoot + Path.DirectorySeparatorChar;
            if (!Directory.Exists(backupRoot))
                Directory.CreateDirectory(backupRoot);

            var destDir = CombineUnderDirectory(backupRootPrefix, category);
            if (!Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);

            var destDirPrefix = Path.EndsInDirectorySeparator(destDir)
                ? destDir
                : destDir + Path.DirectorySeparatorChar;
            var safeFileName = GetSafeBackupFileName(id, fileName);
            destPath = CombineUnderDirectory(destDirPrefix, safeFileName);
            var counter = 2;
            while (System.IO.File.Exists(destPath))
            {
                var safeBaseName = Path.GetFileNameWithoutExtension(safeFileName);
                destPath = CombineUnderDirectory(destDirPrefix, $"{safeBaseName} ({counter}).nzb");
                counter++;
            }

            if (!destPath.StartsWith(destDirPrefix, StringComparison.Ordinal))
                throw new ArgumentException("The NZB backup file must stay within its category directory.");

            await using var src = BlobStore.ReadBlob(id);
            await using var dst = System.IO.File.Create(destPath);
            await src!.CopyToAsync(dst, ct).ConfigureAwait(false);
            return destPath;
        }
        catch (Exception e) when (!e.IsCancellationException(ct) && e is not OutOfMemoryException)
        {
            TryDeleteBackupFile(destPath);
            throw new InvalidOperationException($"Could not save nzb to `{backupLocation}`", e);
        }
        catch
        {
            TryDeleteBackupFile(destPath);
            throw;
        }
    }

    private static void TryDeleteBackupFile(string? backupPath)
    {
        if (backupPath is null) return;
        try
        {
            if (System.IO.File.Exists(backupPath))
                System.IO.File.Delete(backupPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning(e, "Could not delete NZB backup file {BackupPath} after a submission failure", backupPath);
        }
    }

    internal static string GetSafeBackupFileName(Guid id, string fileName)
    {
        ValidateBackupLeafName(fileName);
        var leafName = Path.GetFileName(fileName);
        var baseName = Path.GetFileNameWithoutExtension(leafName);
        if (string.IsNullOrWhiteSpace(baseName)) baseName = id.ToString();
        return $"{baseName}.nzb";
    }

    /// <summary>
    /// Join a single validated leaf onto <paramref name="directoryPrefix"/> and
    /// reject the result unless it stays inside that directory.
    /// </summary>
    internal static string CombineUnderDirectory(string directoryPrefix, string leafName)
    {
        ValidateBackupSegment(leafName, nameof(leafName), "The NZB backup path must be a single file or directory name.");
        var destPath = Path.GetFullPath(Path.Join(directoryPrefix, leafName));
        var relative = Path.GetRelativePath(
            Path.TrimEndingDirectorySeparator(directoryPrefix),
            destPath);
        if (relative is "." or ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new ArgumentException("The NZB backup path must stay within the configured directory.");
        }

        return destPath;
    }

    private static void ValidateBackupLeafName(string fileName)
        => ValidateBackupSegment(fileName, nameof(fileName), "The NZB backup file name must be a single file name.");

    private static void ValidateBackupCategory(string category)
        => ValidateBackupSegment(category, nameof(category), "The NZB backup category must be a single directory name.");

    private static void ValidateBackupSegment(string value, string paramName, string message)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            Path.IsPathRooted(value) ||
            value is "." or ".." ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            value.Contains('/', StringComparison.Ordinal) ||
            value.Contains('\\', StringComparison.Ordinal) ||
            value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException(message, paramName);
        }
    }
}
