using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.MediaLibrary;
using NzbWebDAV.Services.Library;
using NzbWebDAV.Services.Plex;

namespace NzbWebDAV.Tests.Services;

public sealed class LibraryCatalogServiceTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Join(Path.GetTempPath(), $"nzbdav-catalog-tests-{Guid.NewGuid():N}.sqlite");
    private DavDatabaseContext _context = null!;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .AddInterceptors(new SqliteForeignKeyEnabler())
            .ReplaceService<
                IMigrationsSqlGenerator,
                SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;
        _context = new DavDatabaseContext(options);
        await _context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        File.Delete(_databasePath);
    }

    [Fact]
    public async Task Query_InternalItemWithTwoMappings_ReturnsOneRowWithTwoMappings()
    {
        var item = DavItem.New(
            Guid.NewGuid(), DavItem.ContentFolder, "film.mkv", 100,
            DavItem.ItemType.UsenetFile, DavItem.ItemSubType.NzbFile,
            null, null, null, null);
        _context.Items.Add(item);
        _context.LinkMaps.AddRange(
            new LibraryLinkMap
            {
                Id = Guid.NewGuid(), DavItemId = item.Id, LinkPath = "movies/film.mkv",
                TargetText = $"/mnt/.ids/{item.Id}.mkv", MappingType = LibraryMappingType.Internal,
                Status = LibraryLinkStatus.Valid, LastSeenUtc = DateTime.UtcNow,
            },
            new LibraryLinkMap
            {
                Id = Guid.NewGuid(), DavItemId = item.Id, LinkPath = "movies/film.srt",
                TargetText = $"/mnt/.ids/{item.Id}.srt", MappingType = LibraryMappingType.Internal,
                Status = LibraryLinkStatus.Valid, LastSeenUtc = DateTime.UtcNow,
            });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var service = new LibraryCatalogService(_context);
        var result = await service.QueryAsync(new LibraryCatalogQuery { Page = 1, PageSize = 25 });

        var row = Assert.Single(result.Items, x => x.DavItemId == item.Id);
        Assert.Equal(2, row.Mappings.Count);
        Assert.Equal(100, row.Size);

        var searched = await service.QueryAsync(new LibraryCatalogQuery
        {
            Search = "film.srt", Page = 1, PageSize = 25,
        });
        Assert.Equal(2, Assert.Single(searched.Items).Mappings.Count);
    }

    [Fact]
    public async Task VideoOnly_FiltersInternalAndExternalCatalogItems()
    {
        foreach (var name in new[] { "film.mkv", "notes.nfo" })
            _context.Items.Add(DavItem.New(Guid.NewGuid(), DavItem.ContentFolder, name, 100,
                DavItem.ItemType.UsenetFile, DavItem.ItemSubType.NzbFile,
                null, null, null, null));
        _context.LinkMaps.Add(new LibraryLinkMap
        {
            Id = Guid.NewGuid(), LinkPath = "photos/art.jpg", TargetText = "/nas/art.jpg",
            MappingType = LibraryMappingType.External, Status = LibraryLinkStatus.Valid,
            LastSeenUtc = DateTime.UtcNow,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var allFiles = await new LibraryCatalogService(_context).QueryAsync(new LibraryCatalogQuery());
        var videoOnly = await new LibraryCatalogService(_context, videoOnly: true)
            .QueryAsync(new LibraryCatalogQuery());

        Assert.Equal(3, allFiles.TotalCount);
        Assert.Equal("film.mkv", Assert.Single(videoOnly.Items).DisplayName);
    }

    [Fact]
    public async Task Query_ExternalOnlyLink_ReturnsExternalRowWithoutDavItemId()
    {
        _context.LinkMaps.Add(new LibraryLinkMap
        {
            Id = Guid.NewGuid(), DavItemId = null, LinkPath = "docs/old.mkv",
            TargetText = "/old-nas/old.mkv", MappingType = LibraryMappingType.External,
            Status = LibraryLinkStatus.Broken, LastSeenUtc = DateTime.UtcNow,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var service = new LibraryCatalogService(_context);
        var result = await service.QueryAsync(new LibraryCatalogQuery { Page = 1, PageSize = 25 });

        var row = Assert.Single(result.Items);
        Assert.Null(row.DavItemId);
        Assert.Equal("external", row.Kind);
    }

    [Fact]
    public async Task Browse_GroupsAllEpisodesBeforePagination_AndPagesExpandedFiles()
    {
        for (var episode = 1; episode <= 60; episode++)
        {
            var item = DavItem.New(Guid.NewGuid(), DavItem.ContentFolder,
                $"episode-{episode:00}.mkv", 100,
                DavItem.ItemType.UsenetFile, DavItem.ItemSubType.NzbFile,
                null, null, null, null);
            _context.Items.Add(item);
            _context.LinkMaps.Add(new LibraryLinkMap
            {
                Id = Guid.NewGuid(), DavItemId = item.Id,
                LinkPath = $"tv/Example Show/Season 01/Example Show - S01E{episode:00}.mkv",
                TargetText = $"/mnt/.ids/{item.Id}.mkv",
                MappingType = LibraryMappingType.Internal,
                Status = LibraryLinkStatus.Valid, LastSeenUtc = DateTime.UtcNow,
            });
        }
        var other = DavItem.New(Guid.NewGuid(), DavItem.ContentFolder, "other.mkv", 100,
            DavItem.ItemType.UsenetFile, DavItem.ItemSubType.NzbFile,
            null, null, null, null);
        _context.Items.Add(other);
        _context.LinkMaps.Add(new LibraryLinkMap
        {
            Id = Guid.NewGuid(), DavItemId = other.Id,
            LinkPath = "tv/Other Show/Season 01/Other Show - S01E01.mkv",
            TargetText = $"/mnt/.ids/{other.Id}.mkv", MappingType = LibraryMappingType.Internal,
            Status = LibraryLinkStatus.Valid, LastSeenUtc = DateTime.UtcNow,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var metadata = new FakePlexIndex(Enumerable.Range(1, 60)
            .Select(episode => new PlexLibraryMedia($"Example Show - S01E{episode:00}.mkv",
                "episode", $"Episode {episode}", "Example Show", 1, episode, $"e{episode}", "server"))
            .Append(new PlexLibraryMedia("Other Show - S01E01.mkv", "episode",
                "Episode 1", "Other Show", 1, 1, "other", "server")));
        var service = new LibraryBrowseService(new LibraryCatalogService(_context), metadata);
        var first = await service.QueryAsync(new LibraryBrowseQuery
        {
            Category = "shows", PageSize = 1, GroupKey = "shows/Example Show",
        });

        Assert.Equal(2, first.TotalGroups);
        Assert.Equal(61, first.TotalItems);
        Assert.Equal(60, first.Groups[0].ItemCount);
        Assert.NotNull(first.ExpandedGroup);
        Assert.Equal(60, first.ExpandedGroup.TotalItems);
        Assert.Equal(50, first.ExpandedGroup.Items.Count);
        Assert.Equal("S01E01", first.ExpandedGroup.Items[0].Episode);
        Assert.Single(first.ExpandedGroup.Items[0].Item.Mappings);

        var second = await service.QueryAsync(new LibraryBrowseQuery
        {
            Category = "shows", PageSize = 1, GroupKey = "shows/Example Show", GroupPage = 2,
        });
        Assert.Equal(10, second.ExpandedGroup!.Items.Count);
        Assert.Equal("S01E51", second.ExpandedGroup.Items[0].Episode);

        var filePage = await service.QueryAsync(new LibraryBrowseQuery
        {
            View = "files", Category = "all", Page = 2,
        });
        Assert.Equal(61, filePage.TotalFiles);
        Assert.Equal(11, filePage.Files!.Count);
        Assert.Equal(10, filePage.Files.Count(row => row.Title == "Example Show"));
        Assert.Equal(1, filePage.Files.Count(row => row.Title == "Other Show"));

        var filteredFiles = await service.QueryAsync(new LibraryBrowseQuery
        {
            View = "files", Category = "shows", Search = "Episode 60", SeasonFilter = 1,
            MatchFilter = "matched",
        });
        var filtered = Assert.Single(filteredFiles.Files!);
        Assert.Equal(60, filtered.Episode);
        Assert.Equal("Example Show", filtered.Title);
    }

    [Fact]
    public async Task Browse_ClassifiesKnownMovieFolder_ButKeepsExternalAndUnknownVisible()
    {
        var film = DavItem.New(Guid.NewGuid(), DavItem.ContentFolder, "arrival.mkv", 100,
            DavItem.ItemType.UsenetFile, DavItem.ItemSubType.NzbFile,
            null, null, null, null);
        _context.Items.Add(film);
        _context.LinkMaps.AddRange(
            new LibraryLinkMap
            {
                Id = Guid.NewGuid(), DavItemId = film.Id,
                LinkPath = "Movies/Arrival (2016)/Arrival.mkv",
                TargetText = $"/mnt/.ids/{film.Id}.mkv", MappingType = LibraryMappingType.Internal,
                Status = LibraryLinkStatus.Valid, LastSeenUtc = DateTime.UtcNow,
            },
            new LibraryLinkMap
            {
                Id = Guid.NewGuid(), DavItemId = null,
                LinkPath = "TV/External Show/Season 01/Episode.mkv",
                TargetText = "/other/Episode.mkv", MappingType = LibraryMappingType.External,
                Status = LibraryLinkStatus.Valid, LastSeenUtc = DateTime.UtcNow,
            });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var service = new LibraryBrowseService(new LibraryCatalogService(_context),
            new FakePlexIndex([new PlexLibraryMedia("Arrival.mkv", "movie", "Arrival",
                null, null, null, "film", "server")]));
        var movies = await service.QueryAsync(new LibraryBrowseQuery { Category = "movies" });
        var movie = Assert.Single(movies.Groups);
        Assert.Equal("Arrival (2016)", movie.Title);
        Assert.Equal(2, movies.TotalItems);
        Assert.Equal(1, movies.UnmatchedItems);

        var movieFiles = await service.QueryAsync(new LibraryBrowseQuery
        {
            View = "files", Category = "movies",
        });
        var movieFile = Assert.Single(movieFiles.Files!);
        Assert.Equal("Arrival", movieFile.Title);
        Assert.Null(movieFile.Season);
        Assert.Null(movieFile.Episode);

        var unmatched = await service.QueryAsync(new LibraryBrowseQuery
        {
            Category = "unmatched", TypeFilter = "external",
            Search = "External Show",
        });
        Assert.Single(unmatched.Groups);
        Assert.Equal(1, unmatched.TotalItems);
        Assert.Equal(1, unmatched.UnmatchedItems);
    }

    [Fact]
    public async Task Browse_ClassifiesSuffixedLibraryRoots_AndLeavesUnknownRootsUnmatched()
    {
        var paths = new[]
        {
            "TV-4K/All Her Fault/Season 1/All Her Fault - S01E01.mkv",
            "TV-HD/Star Trek - Voyager/Season 4/Star Trek - Voyager - S04E22.avi",
            "TV-Kids/The Amazing World of Gumball/Season 6/Gumball - S06E37.mp4",
            "Movies-4K/Ash (2025)/Ash (2025).mkv",
            "Movies-HD/Project X (2012)/Project X (2012).mkv",
            "TVExtras/Unknown Show/Season 1/Unknown Show - S01E01.mkv",
        };
        foreach (var path in paths)
        {
            var item = DavItem.New(Guid.NewGuid(), DavItem.ContentFolder,
                Path.GetFileName(path), 100, DavItem.ItemType.UsenetFile,
                DavItem.ItemSubType.NzbFile, null, null, null, null);
            _context.Items.Add(item);
            _context.LinkMaps.Add(new LibraryLinkMap
            {
                Id = Guid.NewGuid(), DavItemId = item.Id, LinkPath = path,
                TargetText = $"/mnt/.ids/{item.Id}",
                MappingType = LibraryMappingType.Internal,
                Status = LibraryLinkStatus.Valid, LastSeenUtc = DateTime.UtcNow,
            });
        }
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var metadata = new FakePlexIndex(paths.Take(5).Select((path, index) =>
            new PlexLibraryMedia(Path.GetFileName(path), index < 3 ? "episode" : "movie",
                index < 3 ? "Episode" : Path.GetFileNameWithoutExtension(path),
                index < 3 ? path.Split('/')[1] : null, index < 3 ? 1 : null,
                index < 3 ? index + 1 : null, index.ToString(), "server")));
        var service = new LibraryBrowseService(new LibraryCatalogService(_context), metadata);
        var shows = await service.QueryAsync(new LibraryBrowseQuery { Category = "shows" });
        var movies = await service.QueryAsync(new LibraryBrowseQuery { Category = "movies" });
        var unmatched = await service.QueryAsync(new LibraryBrowseQuery { Category = "unmatched" });

        Assert.Equal(3, shows.TotalGroups);
        Assert.Equal(2, movies.TotalGroups);
        Assert.Equal("Unknown Show - S01E01.mkv", Assert.Single(unmatched.Groups).Title);
        Assert.Equal(1, unmatched.UnmatchedItems);
    }

    [Fact]
    public async Task Browse_UsesPlexTypeEvenWhenFolderIsUnrecognizedOrMisleading()
    {
        var item = DavItem.New(Guid.NewGuid(), DavItem.ContentFolder, "episode.mkv", 100,
            DavItem.ItemType.UsenetFile, DavItem.ItemSubType.NzbFile,
            null, null, null, null);
        _context.Items.Add(item);
        _context.LinkMaps.Add(new LibraryLinkMap
        {
            Id = Guid.NewGuid(), DavItemId = item.Id,
            LinkPath = "Movies-HD/Wrong Folder/Actual Show - S02E03.mkv",
            TargetText = $"/mnt/.ids/{item.Id}.mkv", MappingType = LibraryMappingType.Internal,
            Status = LibraryLinkStatus.Valid, LastSeenUtc = DateTime.UtcNow,
        });
        await _context.SaveChangesAsync();
        var catalog = new LibraryCatalogService(_context);
        var matched = new LibraryBrowseService(catalog,
            new FakePlexIndex([new PlexLibraryMedia("Actual Show - S02E03.mkv", "episode",
                "Episode 3", "Actual Show", 2, 3, "episode", "server")]));
        var shows = await matched.QueryAsync(new LibraryBrowseQuery
        {
            Category = "shows", GroupKey = "shows/Actual Show",
        });
        Assert.Equal("Actual Show", Assert.Single(shows.Groups).Title);
        Assert.Equal("S02E03", Assert.Single(shows.ExpandedGroup!.Items).Episode);
        Assert.Equal("unknown", Assert.Single(shows.ExpandedGroup.Items).Quality);
        Assert.Empty((await matched.QueryAsync(new LibraryBrowseQuery { Category = "shows", Quality = "4k" })).Groups);
        Assert.Equal(0, (await matched.QueryAsync(new LibraryBrowseQuery { Category = "movies" })).TotalGroups);

        var noMatch = new LibraryBrowseService(catalog, new FakePlexIndex([]));
        Assert.Equal(1, (await noMatch.QueryAsync(new LibraryBrowseQuery { Category = "unmatched" })).UnmatchedItems);
        var pending = new LibraryBrowseService(catalog, new FakePlexIndex([], ready: false));
        var pendingResult = await pending.QueryAsync(new LibraryBrowseQuery { Category = "unmatched" });
        Assert.False(pendingResult.PlexStatus.Ready);
        Assert.Equal(0, pendingResult.UnmatchedItems);
        Assert.Empty(pendingResult.Groups);
    }

    [Theory]
    [InlineData("Film.2160p.WEB-DL.mkv", "4k")]
    [InlineData("Film.4K.WEB-DL.mkv", "4k")]
    [InlineData("Film.1080p.WEB-DL.mkv", "1080p")]
    [InlineData("Film.720p.WEB-DL.mkv", "720p")]
    [InlineData("Film.mkv", "unknown")]
    public void Browse_InfersQualityFromReleaseName(string name, string expected)
    {
        Assert.Equal(expected, LibraryBrowseService.QualityFromName(name));
    }

    private sealed class FakePlexIndex(IEnumerable<PlexLibraryMedia> entries, bool ready = true)
        : IPlexLibraryMetadataIndex
    {
        private readonly Dictionary<string, PlexLibraryMedia> _entries = entries.ToDictionary(
            entry => entry.FileName, StringComparer.OrdinalIgnoreCase);

        public PlexLibraryMetadataStatus Status => new(ready,
            ready ? DateTimeOffset.UtcNow : null, _entries.Count, null, false);

        public PlexLibraryMedia? Match(string? fileName) => fileName is not null &&
            _entries.TryGetValue(Path.GetFileName(fileName.Replace('\\', '/')), out var entry) ? entry : null;
    }
}
