using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public sealed class NzbResolutionCachePersistenceTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Join(Path.GetTempPath(), $"nzbdav-resolution-cache-{Guid.NewGuid():N}.sqlite");
    private DbContextOptions<DavDatabaseContext> _options = null!;
    private DavDatabaseContext _context = null!;

    public async Task InitializeAsync()
    {
        _options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .AddInterceptors(new SqliteForeignKeyEnabler())
            .ReplaceService<
                IMigrationsSqlGenerator,
                SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;
        _context = new DavDatabaseContext(_options);
        await _context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        try { File.Delete(_databasePath); } catch { /* best effort */ }
    }

    private NzbResolutionCache NewCache() =>
        new(() => new DavDatabaseContext(_options));

    private static List<NzbResolutionCache.Candidate> MakeCandidates(int count) =>
        Enumerable.Range(0, count).Select(i => new NzbResolutionCache.Candidate
        {
            IndexerName = $"indexer-{i}",
            IndexerUserAgent = "test-agent",
            NzbUrl = $"https://example.com/nzb/{i}",
            Title = $"Title {i}",
            Size = 1000 + i,
        }).ToList();

    [Fact]
    public async Task AddGroupAsync_ThenHydrate_RestoresSharedCandidatesAndStartIndex()
    {
        var candidates = MakeCandidates(3);
        var cacheA = NewCache();
        var tokens = await cacheA.AddGroupAsync(candidates, "movie", "profile-tok", "tt123");

        Assert.Equal(3, tokens.Length);
        Assert.NotNull(cacheA.Get(tokens[1]));
        Assert.Equal(1, cacheA.Get(tokens[1])!.StartIndex);

        var cacheB = NewCache();
        await cacheB.HydrateAsync(TimeSpan.FromDays(7), CancellationToken.None);

        var entry0 = cacheB.Get(tokens[0]);
        var entry1 = cacheB.Get(tokens[1]);
        var entry2 = cacheB.Get(tokens[2]);

        Assert.NotNull(entry0);
        Assert.NotNull(entry1);
        Assert.NotNull(entry2);
        Assert.Equal(0, entry0.StartIndex);
        Assert.Equal(1, entry1.StartIndex);
        Assert.Equal(2, entry2.StartIndex);
        Assert.Equal("https://example.com/nzb/1", entry1.Primary.NzbUrl);
        Assert.Equal("movie", entry1.Type);
        Assert.Equal("profile-tok", entry1.ProfileToken);
        Assert.Equal("tt123", entry1.Id);
        Assert.Same(entry0.Candidates, entry2.Candidates);
    }

    [Fact]
    public async Task HydrateAndPurge_SkipAndRemoveExpiredGroups()
    {
        var cacheWriter = NewCache();
        var freshTokens = await cacheWriter.AddGroupAsync(MakeCandidates(1), "movie", "p", "fresh");

        var oldToken = "deadbeefcafebabe";
        await using (var ctx = new DavDatabaseContext(_options))
        {
            ctx.NzbResolutionGroups.Add(new NzbResolutionGroup
            {
                Id = Guid.NewGuid(),
                Type = "movie",
                ProfileToken = "p",
                SearchId = "old",
                CandidatesJson = System.Text.Json.JsonSerializer.Serialize(MakeCandidates(1)),
                TokensJson = System.Text.Json.JsonSerializer.Serialize(new[] { oldToken }),
                CreatedAtUnix = DateTimeOffset.UtcNow.AddDays(-8).ToUnixTimeMilliseconds(),
            });
            await ctx.SaveChangesAsync();
        }

        // Hydrate with a long TTL so the aged row loads into memory with its old CreatedAt.
        var cache = NewCache();
        await cache.HydrateAsync(TimeSpan.FromDays(30), CancellationToken.None);
        Assert.NotNull(cache.Get(freshTokens[0]));
        Assert.NotNull(cache.Get(oldToken));

        // Short hydrate on a fresh instance should skip the aged row.
        var shortHydrate = NewCache();
        await shortHydrate.HydrateAsync(TimeSpan.FromDays(7), CancellationToken.None);
        Assert.NotNull(shortHydrate.Get(freshTokens[0]));
        Assert.Null(shortHydrate.Get(oldToken));

        // Purge with 7d TTL: evict aged memory entry and delete aged DB row.
        await cache.PurgeExpiredAsync(TimeSpan.FromDays(7), CancellationToken.None);
        Assert.NotNull(cache.Get(freshTokens[0]));
        Assert.Null(cache.Get(oldToken));

        await using (var ctx = new DavDatabaseContext(_options))
        {
            Assert.Equal(0, await ctx.NzbResolutionGroups.CountAsync(g => g.SearchId == "old"));
            Assert.True(await ctx.NzbResolutionGroups.AnyAsync(g => g.SearchId == "fresh"));
        }
    }

    [Fact]
    public async Task AddGroupAsync_PersistenceFailure_StillReturnsTokens()
    {
        var cache = new NzbResolutionCache(() => throw new InvalidOperationException("db down"));
        var candidates = MakeCandidates(2);
        var tokens = await cache.AddGroupAsync(candidates, "series", "p", "tt9");

        Assert.Equal(2, tokens.Length);
        Assert.NotNull(cache.Get(tokens[0]));
        Assert.Equal("https://example.com/nzb/0", cache.Get(tokens[0])!.Primary.NzbUrl);
        Assert.Same(cache.Get(tokens[0])!.Candidates, cache.Get(tokens[1])!.Candidates);
    }
}
