using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Services.Catalog;

namespace NzbWebDAV.Tests.Services.Catalog;

/// <summary>
/// Viewer-profile behavior: the seeded default profile a migrated database always has, name
/// validation, the profile cap, and list ordering.
/// </summary>
public sealed class ViewerProfileTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Join(Path.GetTempPath(), $"viewer-profiles-{Guid.NewGuid():N}.sqlite");
    private readonly MutableTimeProvider _clock = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
    private DbContextOptions<DavDatabaseContext> _options = null!;
    private DavDatabaseContext _context = null!;
    private ViewerProfileService _service = null!;

    public async Task InitializeAsync()
    {
        _options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .AddInterceptors(new SqliteForeignKeyEnabler())
            .ReplaceService<IMigrationsSqlGenerator, SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;
        _context = new DavDatabaseContext(_options);
        await _context.Database.MigrateAsync();
        _service = new ViewerProfileService(() => new DavDatabaseContext(_options), _clock);
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        File.Delete(_databasePath);
    }

    [Fact]
    public async Task MigratedDatabase_SeedsTheDefaultProfile()
    {
        // Admin sessions and legacy watch rows resolve to this profile, so the id the
        // migration lets the provider assign has to be the id the service assumes.
        var profile = Assert.Single(await _service.ListAsync(CancellationToken.None));
        Assert.Equal(ViewerProfileService.DefaultProfileId, profile.Id);
        Assert.Equal("Main", profile.Name);
    }

    [Fact]
    public async Task Create_TrimsTheNameAndStampsTheClock()
    {
        var profile = await _service.CreateAsync("  Kids  ", CancellationToken.None);

        Assert.True(profile.Id > ViewerProfileService.DefaultProfileId);
        Assert.Equal("Kids", profile.Name);
        Assert.Equal(_clock.GetUtcNow().ToUnixTimeSeconds(), profile.CreatedAtEpoch);

        var listed = await _service.ListAsync(CancellationToken.None);
        Assert.Equal(["Main", "Kids"], listed.Select(entry => entry.Name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_RejectsABlankName(string name)
    {
        await Assert.ThrowsAsync<CatalogException>(() => _service.CreateAsync(name, CancellationToken.None));
    }

    [Fact]
    public async Task Create_RejectsANameLongerThanTheColumn()
    {
        var tooLong = new string('a', ViewerProfile.MaxNameLength + 1);

        await Assert.ThrowsAsync<CatalogException>(() => _service.CreateAsync(tooLong, CancellationToken.None));
    }

    [Fact]
    public async Task Create_StopsAtTheProfileCap()
    {
        var existing = (await _service.ListAsync(CancellationToken.None)).Count;
        for (var index = existing; index < ViewerProfileService.MaxProfiles; index++)
        {
            await _service.CreateAsync($"Profile {index}", CancellationToken.None);
        }

        await Assert.ThrowsAsync<CatalogException>(
            () => _service.CreateAsync("One too many", CancellationToken.None));
    }

    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
