using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using Serilog;

namespace NzbWebDAV.Services;

public class NzbResolutionCache(Func<DavDatabaseContext> contextFactory)
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    /// <summary>
    /// Register a candidate group and return one token per candidate. Each token's entry
    /// points at the same ordered Candidates list with its own StartIndex, so the play
    /// handler can iterate from any starting position for fast-fail + fallback.
    /// Persists the group to SQLite so tokens survive restarts.
    /// </summary>
    public async Task<string[]> AddGroupAsync(
        IReadOnlyList<Candidate> candidates, string type, string profileToken, string id)
    {
        var tokens = new string[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
            tokens[i] = GenerateToken();

        var createdAt = DateTime.UtcNow;
        try
        {
            await using var ctx = contextFactory();
            ctx.NzbResolutionGroups.Add(new NzbResolutionGroup
            {
                Id = Guid.NewGuid(),
                Type = type,
                ProfileToken = profileToken,
                SearchId = id,
                CandidatesJson = JsonSerializer.Serialize(candidates),
                TokensJson = JsonSerializer.Serialize(tokens),
                CreatedAtUnix = new DateTimeOffset(createdAt).ToUnixTimeMilliseconds(),
            });
            await ctx.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or JsonException)
        {
            Log.Warning(ex, "Failed to persist play-token group; tokens will not survive a restart");
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            _entries[tokens[i]] = new Entry
            {
                Candidates = candidates,
                StartIndex = i,
                Type = type,
                ProfileToken = profileToken,
                Id = id,
                CreatedAt = createdAt,
            };
        }

        return tokens;
    }

    public Entry? Get(string token) => _entries.TryGetValue(token, out var e) ? e : null;

    /// <summary>
    /// Load non-expired groups from SQLite into the in-memory dictionary.
    /// Deserializes each group's candidates list once and shares it across that group's tokens.
    /// </summary>
    public virtual async Task HydrateAsync(TimeSpan ttl, CancellationToken ct)
    {
        var cutoffUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)ttl.TotalMilliseconds;
        await using var ctx = contextFactory();
        var rows = await ctx.NzbResolutionGroups
            .AsNoTracking()
            .Where(g => g.CreatedAtUnix >= cutoffUnixMs)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var loaded = 0;
        foreach (var row in rows)
        {
            try
            {
                var candidates = JsonSerializer.Deserialize<List<Candidate>>(row.CandidatesJson);
                var tokens = JsonSerializer.Deserialize<string[]>(row.TokensJson);
                if (candidates is null || tokens is null || tokens.Length != candidates.Count)
                {
                    Log.Debug(
                        "Skipping resolution group {Id}: candidates/tokens deserialize mismatch",
                        row.Id);
                    continue;
                }

                var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(row.CreatedAtUnix).UtcDateTime;
                for (var i = 0; i < tokens.Length; i++)
                {
                    _entries[tokens[i]] = new Entry
                    {
                        Candidates = candidates,
                        StartIndex = i,
                        Type = row.Type,
                        ProfileToken = row.ProfileToken,
                        Id = row.SearchId,
                        CreatedAt = createdAt,
                    };
                }

                loaded++;
            }
            catch (JsonException ex)
            {
                Log.Debug(ex, "Skipping resolution group {Id}: failed to deserialize", row.Id);
            }
        }

        if (loaded > 0)
            Log.Information("Hydrated {Groups} play-token group(s) ({Tokens} tokens)", loaded, _entries.Count);
    }

    /// <summary>
    /// Evict expired in-memory entries and delete expired rows from SQLite.
    /// </summary>
    public async Task PurgeExpiredAsync(TimeSpan ttl, CancellationToken ct)
    {
        var cutoffDateTime = DateTime.UtcNow - ttl;
        var cutoffUnixMs = new DateTimeOffset(cutoffDateTime).ToUnixTimeMilliseconds();

        foreach (var kv in _entries)
        {
            if (kv.Value.CreatedAt < cutoffDateTime)
                _entries.TryRemove(kv.Key, out _);
        }

        await using var ctx = contextFactory();
        // Identifiers stay quoted: PostgreSQL folds unquoted names to lowercase, but
        // EF Core creates the table as "NzbResolutionGroups" (case-sensitive).
        await ctx.Database.ExecuteSqlRawAsync(
            "DELETE FROM \"NzbResolutionGroups\" WHERE \"CreatedAtUnix\" < {0}",
            cutoffUnixMs)
            .ConfigureAwait(false);
    }

    private static string GenerateToken()
    {
        var bytes = new byte[8];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexStringLower(bytes);
    }

    public class Candidate
    {
        public required string IndexerName { get; init; }
        public required string IndexerUserAgent { get; init; }
        public required string NzbUrl { get; init; }
        public required string Title { get; init; }
        public long Size { get; init; }
        public DateTimeOffset? Posted { get; init; }
        public DateTimeOffset? UsenetDate { get; init; }
        public string? Poster { get; init; }
        public int? Grabs { get; init; }
        public int? Password { get; init; }
        public string? ProxyUrl { get; init; }
        public string? SourceIndexerName { get; init; }
        public string? Language { get; init; }
        public string? Subs { get; init; }
        public string? InfoHash { get; init; }
        public bool VerifiedAvailable { get; set; }
    }

    public class Entry
    {
        public required IReadOnlyList<Candidate> Candidates { get; init; }
        public required int StartIndex { get; init; }
        public required string Type { get; init; }
        public required string ProfileToken { get; init; }
        public required string Id { get; init; }
        public required DateTime CreatedAt { get; init; }

        public Candidate Primary => Candidates[StartIndex];
    }
}
