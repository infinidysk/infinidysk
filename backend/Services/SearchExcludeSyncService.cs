using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Background service that keeps the synced exclude-pattern lists fresh.
///
/// On a timer (and immediately whenever the configured URLs change) it fetches each
/// <c>search.exclude-sync-urls</c> entry, parses the regex patterns, and persists a
/// last-good snapshot to <c>search.exclude-sync-cache</c>. <see cref="ConfigManager"/>
/// merges that snapshot ahead of the manual patterns when it builds the compiled
/// exclude list, so synced patterns take precedence and the manual list is appended
/// after (with exact duplicates removed). A failed fetch keeps the previous snapshot,
/// so the filter never empties because a source was briefly unreachable.
/// </summary>
public sealed class SearchExcludeSyncService : BackgroundService
{
    private const string CacheKey = "search.exclude-sync-cache";
    private const long MaxDownloadBytes = 16L * 1024 * 1024; // pattern lists are small
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly ConfigManager _configManager;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SearchExcludeSyncService(ConfigManager configManager)
    {
        _configManager = configManager;
        _configManager.OnConfigChanged += OnConfigChanged;
    }

    public override void Dispose()
    {
        _configManager.OnConfigChanged -= OnConfigChanged;
        _gate.Dispose();
        base.Dispose();
    }

    private void OnConfigChanged(object? sender, ConfigManager.ConfigEventArgs e)
    {
        // React only to the inputs — NOT to our own cache writes (which would loop).
        if (!e.ChangedConfig.ContainsKey(ConfigKeys.SearchExcludeSyncUrls)
            && !e.ChangedConfig.ContainsKey(ConfigKeys.SearchExcludeSyncRefreshMinutes)) return;

        // A freshly-set URL should apply within seconds, not at the next tick.
        _ = Task.Run(async () =>
        {
            try { await RefreshAllAsync(force: true, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { Log.Debug(ex, "Exclude-sync: post-config refresh failed"); }
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RefreshAllAsync(force: false, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception e) { Log.Debug(e, "Exclude-sync: refresh loop error"); }

            try { await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Current per-URL status, read from the persisted cache (no network).</summary>
    public IReadOnlyList<ExcludeSyncStatus> GetStatus()
    {
        var cache = _configManager.GetSearchExcludeSyncCache();
        return _configManager.GetSearchExcludeSyncUrls().Select(url =>
        {
            cache.Urls.TryGetValue(url, out var entry);
            return new ExcludeSyncStatus(
                url,
                entry?.Items.Count ?? 0,
                entry is { FetchedAt: > 0 } ? entry.FetchedAt : null,
                entry is { LastChecked: > 0 } ? entry.LastChecked : null,
                entry?.Error);
        }).ToList();
    }

    /// <summary>
    /// Fetch the configured URLs (those that are due, unless <paramref name="force"/>),
    /// persist the merged snapshot, and return the resulting per-URL status.
    /// </summary>
    public async Task<IReadOnlyList<ExcludeSyncStatus>> RefreshAllAsync(bool force, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var urls = _configManager.GetSearchExcludeSyncUrls();
            var cache = _configManager.GetSearchExcludeSyncCache();
            var refreshSeconds = (long)_configManager.GetSearchExcludeSyncRefreshMinutes() * 60;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var changed = false;

            // Forget cache entries for URLs that are no longer configured.
            var configured = new HashSet<string>(urls, StringComparer.Ordinal);
            foreach (var stale in cache.Urls.Keys.Where(k => !configured.Contains(k)).ToList())
            {
                cache.Urls.Remove(stale);
                changed = true;
            }

            foreach (var url in urls)
            {
                ct.ThrowIfCancellationRequested();
                cache.Urls.TryGetValue(url, out var entry);
                if (!force && entry is not null && now - entry.LastChecked < refreshSeconds) continue;
                cache.Urls[url] = await FetchAsync(url, entry, now, ct).ConfigureAwait(false);
                changed = true;
            }

            if (changed) await PersistAsync(cache, ct).ConfigureAwait(false);
            return GetStatus();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<ExcludeSyncUrlEntry> FetchAsync(
        string url, ExcludeSyncUrlEntry? prev, long now, CancellationToken ct)
    {
        // Start from the previous snapshot so a failure or 304 keeps the last-good items.
        var entry = new ExcludeSyncUrlEntry
        {
            Items = prev?.Items ?? new List<string>(),
            FetchedAt = prev?.FetchedAt ?? 0,
            Etag = prev?.Etag,
            LastChecked = now,
        };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(prev?.Etag))
                req.Headers.TryAddWithoutValidation("If-None-Match", prev.Etag);

            using var resp = await Http
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.NotModified)
            {
                entry.Error = null;
                return entry;
            }
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var body = await ReadCappedAsync(stream, ct).ConfigureAwait(false);

            // Keep only patterns that actually compile, so the stored snapshot, the count
            // shown in the UI, and the log line all reflect usable patterns — not raw lines
            // that ExcludePatternParser would silently drop on every rebuild.
            var valid = ParsePayload(body)
                .Where(p => ExcludePatternParser.Parse(p) is not null)
                .ToList();

            // Don't let an empty or all-invalid payload wipe a previously-good snapshot;
            // keep the prior items/FetchedAt/Etag and surface the problem instead.
            if (valid.Count == 0)
            {
                entry.Error = "Source returned no usable patterns; keeping the last good copy.";
                return entry;
            }

            entry.Items = valid;
            entry.FetchedAt = now;
            entry.Etag = resp.Headers.ETag?.ToString();
            entry.Error = null;
            Log.Information("Exclude-sync: refreshed {Url} ({Count} patterns)", url, valid.Count);
            return entry;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            entry.Error = e.Message;
            Log.Warning("Exclude-sync: failed to fetch {Url}: {Message} (keeping last-good)", url, e.Message);
            return entry;
        }
    }

    /// <summary>
    /// Accepts the two formats community lists publish:
    ///   - <c>{ "values": ["regex", ...] }</c>
    ///   - <c>[{ "pattern": "regex", "name": "...", "score": 0 }, ...]</c> (score ignored)
    /// A bare <c>["regex", ...]</c> array is tolerated too.
    /// </summary>
    internal static List<string> ParsePayload(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var items = new List<string>();

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in root.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    items.Add(el.GetString()!);
                }
                else if (el.ValueKind == JsonValueKind.Object
                         && el.TryGetProperty("pattern", out var pat)
                         && pat.ValueKind == JsonValueKind.String)
                {
                    items.Add(pat.GetString()!);
                }
            }
            return items;
        }

        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("values", out var values)
            && values.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in values.EnumerateArray().Where(el => el.ValueKind == JsonValueKind.String))
                items.Add(el.GetString()!);
            return items;
        }

        throw new InvalidOperationException(
            "Unexpected format. Expected {\"values\":[...]} or [{\"pattern\":\"...\"}].");
    }

    private static async Task<string> ReadCappedAsync(Stream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > MaxDownloadBytes)
                throw new InvalidOperationException("Synced list exceeds the size limit.");
            await ms.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    private async Task PersistAsync(ExcludeSyncCache cache, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(cache);

        await using var dbContext = new DavDatabaseContext();
        var existing = await dbContext.ConfigItems
            .FirstOrDefaultAsync(x => x.ConfigName == CacheKey, ct).ConfigureAwait(false);
        if (existing is null)
            dbContext.ConfigItems.Add(new ConfigItem { ConfigName = CacheKey, ConfigValue = json });
        else
            existing.ConfigValue = json;
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        // Refresh the in-memory config and invalidate the compiled exclude-pattern cache.
        _configManager.UpdateValues(new List<ConfigItem>
        {
            new() { ConfigName = CacheKey, ConfigValue = json },
        });
    }
}

/// <summary>Per-URL sync status surfaced to the settings UI.</summary>
public sealed record ExcludeSyncStatus(
    string Url,
    int Count,
    long? FetchedAt,
    long? LastChecked,
    string? Error);
