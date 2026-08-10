using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// One-shot startup repair for multipart DavItems that still advertise
/// <see cref="long.MaxValue"/> as FileSize (issue #537).
/// </summary>
public class MultipartFileSizeRepairService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var ctx = new DavDatabaseContext();
            await RepairAsync(ctx, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutdown
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Error(e, "Unexpected error repairing multipart FileSize sentinels: {Message}", e.Message);
        }
    }

    internal static async Task<(int Repaired, int SkippedLazy, int SkippedUnsafe, int Missing)> RepairAsync(
        DavDatabaseContext ctx,
        CancellationToken ct)
    {
        var dbClient = new DavDatabaseClient(ctx);

        var broken = await ctx.Items
            .AsNoTracking()
            .Where(i =>
                i.Type == DavItem.ItemType.UsenetFile
                && i.SubType == DavItem.ItemSubType.MultipartFile
                && i.FileSize == long.MaxValue)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (broken.Count == 0)
        {
            Log.Information("MultipartFileSizeRepair: no Int64.MaxValue FileSize rows found");
            return (0, 0, 0, 0);
        }

        var repaired = 0;
        var skippedLazy = 0;
        var skippedUnsafe = 0;
        var missing = 0;

        foreach (var item in broken)
        {
            ct.ThrowIfCancellationRequested();

            DavMultipartFile? multipart;
            try
            {
                multipart = await dbClient.GetDavMultipartFileAsync(item, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (!e.IsCancellationException(ct) && e is not OutOfMemoryException)
            {
                missing++;
                Log.Warning(
                    "MultipartFileSizeRepair: failed to load multipart for {ItemId} ({Path}). Reason: {Reason}",
                    item.Id, item.Path, e.Message);
                Log.Debug(e, "MultipartFileSizeRepair load failure stack");
                continue;
            }

            if (multipart?.Metadata is null)
            {
                missing++;
                Log.Warning(
                    "MultipartFileSizeRepair: missing multipart blob for {ItemId} ({Path})",
                    item.Id, item.Path);
                continue;
            }

            var meta = multipart.Metadata;
            if (meta.IsLazy || (meta.PendingParts?.Length ?? 0) > 0)
            {
                skippedLazy++;
                continue;
            }

            var size = MultipartFileSizeReconciler.TryGetPublishedSize(meta);
            if (size is null)
            {
                skippedUnsafe++;
                Log.Warning(
                    "MultipartFileSizeRepair: cannot safely repair encrypted/unknown size for {ItemId} ({Path})",
                    item.Id, item.Path);
                continue;
            }

            var publishedSize = size.Value;
            var updated = await ctx.Items
                .Where(i => i.Id == item.Id && i.FileSize == long.MaxValue)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.FileSize, publishedSize), ct)
                .ConfigureAwait(false);
            if (updated > 0)
                repaired++;
        }

        Log.Information(
            "MultipartFileSizeRepair: repaired={Repaired} skippedLazy={SkippedLazy} " +
            "skippedUnsafe={SkippedUnsafe} missing={Missing} candidates={Candidates}",
            repaired, skippedLazy, skippedUnsafe, missing, broken.Count);

        return (repaired, skippedLazy, skippedUnsafe, missing);
    }
}
