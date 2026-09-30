using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using NzbWebDAV.Database;

namespace NzbWebDAV.Services.NativeCache;

/// <summary>Flushes bounded traffic deltas and backfills legacy display names off the playback path.</summary>
public sealed class NativeCacheBrowserMaintenance(NativeCacheService native, IServiceScopeFactory scopes) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                await TickAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            // Shutdown is bounded: a stalled NAS must not hold the host, while the
            // catalogue itself is local and ordinarily completes promptly.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { await FlushAsync(deadline.Token).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { Log.Warning("Native cache traffic flush on shutdown failed ({Reason})", exception.GetType().Name); }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        if (!await native.WaitForInitializationAsync(ct).ConfigureAwait(false) || native.Store is not { } store) return;
        await FlushAsync(ct).ConfigureAwait(false);
        try
        {
            await store.PruneBrowserHistoryAsync(ct).ConfigureAwait(false);
            var ids = await store.GetUnnamedItemIdsAsync(100, ct).ConfigureAwait(false);
            var parsed = ids.Select(id => Guid.TryParse(id, out var guid) ? guid : Guid.Empty)
                .Where(id => id != Guid.Empty).ToArray();
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            if (parsed.Length > 0)
            {
                using var scope = scopes.CreateScope();
                var database = scope.ServiceProvider.GetRequiredService<DavDatabaseClient>();
                var items = await database.GetItemsByIdsBatchedAsync(parsed, ct: ct).ConfigureAwait(false);
                foreach (var item in items) names[item.Id.ToString("N")] = item.Name;
            }
            foreach (var id in ids) names.TryAdd(id, id);
            await store.UpdateDisplayNamesAsync(names, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        { Log.Warning("Native cache browser maintenance failed ({Reason})", exception.GetType().Name); }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        if (native.Store is not { } store) return;
        var rows = native.Statistics.DrainTraffic();
        try { await store.SaveTrafficAsync(rows, ct).ConfigureAwait(false); }
        catch
        {
            native.Statistics.RestoreTraffic(rows);
            throw;
        }
    }
}
