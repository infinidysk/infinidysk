using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.UsenetMigration.Model;
using NzbWebDAV.UsenetMigration.Nzb;
using NzbWebDAV.UsenetMigration.Source;
using NzbWebDAV.Api.SabControllers.AddFile;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Database.Models.UsenetMigration;
using NzbWebDAV.Queue;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.UsenetMigration.Runner;

/// <summary>
/// Submits pending releases into NzbDAV's own SAB pipeline, in-process, up to the
/// session's queue-depth gate. Rebuilds each release's NZB from
/// its <c>.nzbz</c> store, re-injects the encryption head, and calls
/// <see cref="AddFileController.AddFileAsync"/> directly — the controller reads
/// only the <see cref="AddFileRequest"/>, not the HttpContext, so a bare
/// <see cref="DefaultHttpContext"/> suffices.
///
/// Submission is sequential by default because concurrent submissions sharing a
/// <c>UNIQUE(Category, FileName)</c> key can evict one another mid-download.
/// </summary>
public sealed class SubmissionWorkerPool(
    UsenetMigrationStore store,
    QueueManager queueManager,
    ConfigManager configManager,
    WebsocketManager websocketManager)
{
    /// <summary>
    /// Submits as many pending releases as the queue-depth gate allows, oldest
    /// first. Returns the number submitted this pass.
    /// </summary>
    public async Task<int> SubmitBatchAsync(CancellationToken ct = default)
    {
        var session = await store.GetSessionAsync(ct).ConfigureAwait(false);
        var maxDepth = Math.Max(1, session.MaxQueueDepth);

        var depth = await CurrentQueueDepthAsync(ct).ConfigureAwait(false);
        if (depth >= maxDepth)
            return 0;

        await using var ctx = store.NewContext();
        var pending = await ctx.Submissions
            .Where(s => s.State == "pending")
            .OrderBy(s => s.StoreRef)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (pending.Count == 0)
            return 0;

        var submitted = 0;
        foreach (var sub in pending)
        {
            ct.ThrowIfCancellationRequested();
            if (depth >= maxDepth)
                break;

            var release = await ctx.Releases
                .FirstOrDefaultAsync(r => r.StoreRef == sub.StoreRef, ct)
                .ConfigureAwait(false);
            if (release is null || string.IsNullOrEmpty(release.TargetCategory))
            {
                sub.State = "failed";
                sub.Error = "Release missing or has no target category at submit time.";
                sub.UpdatedAt = DateTime.UtcNow;
                continue;
            }

            try
            {
                var nzoId = await SubmitReleaseAsync(release, session, ctx, ct).ConfigureAwait(false);
                sub.NzoId = nzoId;
                sub.State = "submitted";
                sub.SubmittedAt = DateTime.UtcNow;
                sub.Attempt++;
                depth++;
                submitted++;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log.Warning(e, "Failed to submit migration release {StoreRef}: {Message}",
                    release.StoreRef, e.Message);
                sub.State = "failed";
                sub.Error = e.Message;
                sub.Attempt++;
            }

            sub.UpdatedAt = DateTime.UtcNow;
        }

        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        return submitted;
    }

    private async Task<string> SubmitReleaseAsync(
        MigrationRelease release,
        MigrationSessionState session,
        UsenetMigrationDbContext ctx,
        CancellationToken ct)
    {
        var storePath = StoreLocator.Resolve(release.StoreRef, session.AltmountStoreRoot)
                        ?? throw new InvalidOperationException(
                            $"Store '{release.StoreRef}' is no longer readable at submit time.");

        var nzbStore = await AltmountStoreReader.ReadStoreAsync(storePath, ct).ConfigureAwait(false);
        var nzbBytes = NzbXmlBuilder.Build(nzbStore);

        if (release.HasPassword || release.Encryption is not null)
        {
            var encryptionMeta = await LoadEncryptionMetaAsync(release.StoreRef, ctx, ct).ConfigureAwait(false);
            if (encryptionMeta is not null)
                nzbBytes = EncryptionHeadInjector.Inject(nzbBytes, encryptionMeta);
        }

        await using var dbCtx = new DavDatabaseContext();
        var dbClient = new DavDatabaseClient(dbCtx);
        var controller = new AddFileController(
            new DefaultHttpContext(), dbClient, queueManager, configManager, websocketManager);

        var request = new AddFileRequest
        {
            // QueueFileName already carries the resolved ".nzb" filename that lands
            // in QueueItem.FileName, so do not resolve it a second time.
            FileName = release.QueueFileName,
            NzbFileStream = new MemoryStream(nzbBytes),
            Category = release.TargetCategory!,
            Priority = QueueItem.PriorityOption.Low,
            PostProcessing = QueueItem.PostProcessingOption.None,
            CancellationToken = ct,
        };

        var response = await controller.AddFileAsync(request).ConfigureAwait(false);
        if (response.NzoIds.Count == 0)
            throw new InvalidOperationException("AddFileAsync returned no nzo id.");

        return response.NzoIds[0];
    }

    /// <summary>
    /// Reads the first virtual file's meta that actually carries encryption or a
    /// password, for the head injection. Only reached for encrypted/passworded
    /// releases, so the extra disk reads are rare.
    /// </summary>
    private static async Task<AltmountFileMetadata?> LoadEncryptionMetaAsync(
        string storeRef, UsenetMigrationDbContext ctx, CancellationToken ct)
    {
        var metaPaths = await ctx.ReleaseFiles.AsNoTracking()
            .Where(f => f.StoreRef == storeRef)
            .OrderBy(f => f.Id)
            .Select(f => f.MetaPath)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var metaPath in metaPaths)
        {
            AltmountFileMetadata meta;
            try
            {
                meta = await AltmountMetaReader.ReadAsync(metaPath, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                continue;
            }

            if (meta.Encryption != AltmountEncryption.None || !string.IsNullOrEmpty(meta.Password))
                return meta;
        }

        return null;
    }

    /// <summary>
    /// Current NzbDAV queue depth. <see cref="QueueManager"/> has no depth accessor,
    /// so this counts <c>QueueItems</c> directly.
    /// </summary>
    private static async Task<int> CurrentQueueDepthAsync(CancellationToken ct)
    {
        await using var davCtx = new DavDatabaseContext();
        return await davCtx.QueueItems.AsNoTracking().CountAsync(ct).ConfigureAwait(false);
    }
}
