using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.MediaLibrary;
using NzbWebDAV.Services.Library;

namespace NzbWebDAV.Tests.Services;

public sealed class LibraryCatalogScannerTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Join(Path.GetTempPath(), $"nzbdav-scanner-tests-{Guid.NewGuid():N}.sqlite");
    private readonly string _libraryRoot =
        Path.Join(Path.GetTempPath(), $"nzbdav-library-{Guid.NewGuid():N}");
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_libraryRoot);
        var services = new ServiceCollection();
        services.AddDbContextFactory<DavDatabaseContext>(options =>
            options.UseSqlite($"Data Source={_databasePath}")
                .AddInterceptors(new SqliteForeignKeyEnabler())
                .ReplaceService<
                    IMigrationsSqlGenerator,
                    SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>());
        _provider = services.BuildServiceProvider();
        await using var context = _provider.GetRequiredService<IDbContextFactory<DavDatabaseContext>>().CreateDbContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        Directory.Delete(_libraryRoot, recursive: true);
        File.Delete(_databasePath);
    }

    [Fact]
    public async Task ReconcileOnce_ExternalSymlink_UpsertsExternalRow()
    {
        var target = Path.Join(_libraryRoot, "real.mkv");
        await File.WriteAllTextAsync(target, "x");
        File.CreateSymbolicLink(Path.Join(_libraryRoot, "linked.mkv"), target);
        var config = new ConfigManager();
        config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.MediaLibraryDir, ConfigValue = _libraryRoot }]);
        var factory = _provider.GetRequiredService<IDbContextFactory<DavDatabaseContext>>();
        var scanner = new LibraryCatalogScanner(config, factory);

        await scanner.ReconcileOnceAsync(CancellationToken.None);

        await using var context = factory.CreateDbContext();
        var row = await context.LinkMaps.SingleAsync();
        Assert.Equal("linked.mkv", row.LinkPath);
        Assert.Equal(LibraryMappingType.External, row.MappingType);
        Assert.Null(row.DavItemId);
        Assert.Equal(LibraryLinkStatus.Valid, row.Status);
    }

    [Fact]
    public async Task ReconcileOnce_MissingExternalTarget_IsBrokenOnlyWhenParentIsVisible()
    {
        var visibleParent = Path.Join(_libraryRoot, "visible");
        Directory.CreateDirectory(visibleParent);
        File.CreateSymbolicLink(Path.Join(_libraryRoot, "missing-file.mkv"),
            Path.Join(visibleParent, "missing.mkv"));
        File.CreateSymbolicLink(Path.Join(_libraryRoot, "unavailable-mount.mkv"),
            Path.Join(_libraryRoot, "unavailable", "missing.mkv"));
        var config = new ConfigManager();
        config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.MediaLibraryDir, ConfigValue = _libraryRoot }]);
        var factory = _provider.GetRequiredService<IDbContextFactory<DavDatabaseContext>>();
        var scanner = new LibraryCatalogScanner(config, factory);

        await scanner.ReconcileOnceAsync(CancellationToken.None);

        await using var context = factory.CreateDbContext();
        var rows = await context.LinkMaps.ToDictionaryAsync(row => row.LinkPath);
        Assert.Equal(LibraryLinkStatus.Broken, rows["missing-file.mkv"].Status);
        Assert.Equal(LibraryLinkStatus.Unchecked, rows["unavailable-mount.mkv"].Status);
    }

    [Fact]
    public async Task VideoOnly_ExcludesNonVideoLinksAndRestoresThemWhenDisabled()
    {
        var video = Path.Join(_libraryRoot, "video.mkv");
        var subtitle = Path.Join(_libraryRoot, "subtitle.srt");
        await File.WriteAllTextAsync(video, "video");
        await File.WriteAllTextAsync(subtitle, "subtitle");
        File.CreateSymbolicLink(Path.Join(_libraryRoot, "linked.mkv"), video);
        File.CreateSymbolicLink(Path.Join(_libraryRoot, "linked.srt"), subtitle);
        await File.WriteAllTextAsync(Path.Join(_libraryRoot, "movie.strm"), "https://media.test/film.mkv");
        await File.WriteAllTextAsync(Path.Join(_libraryRoot, "subtitle.strm"), "https://media.test/subtitle.srt");
        var config = new ConfigManager();
        config.UpdateValues([
            new ConfigItem { ConfigName = ConfigKeys.MediaLibraryDir, ConfigValue = _libraryRoot },
            new ConfigItem { ConfigName = ConfigKeys.MediaLibraryVideoOnly, ConfigValue = "true" },
        ]);
        var factory = _provider.GetRequiredService<IDbContextFactory<DavDatabaseContext>>();
        var scanner = new LibraryCatalogScanner(config, factory);

        await scanner.ReconcileOnceAsync(CancellationToken.None);
        await using (var context = factory.CreateDbContext())
        {
            var paths = await context.LinkMaps.Select(map => map.LinkPath).ToListAsync();
            Assert.Equal(2, paths.Count);
            Assert.Contains("linked.mkv", paths);
            Assert.Contains("movie.strm", paths);
        }

        config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.MediaLibraryVideoOnly, ConfigValue = "false" }]);
        await scanner.ReconcileOnceAsync(CancellationToken.None);
        await using var restored = factory.CreateDbContext();
        Assert.Equal(4, await restored.LinkMaps.CountAsync());
        Assert.True(File.Exists(subtitle));
    }

    [Fact]
    public async Task DisabledLibrary_DoesNotScanOrMarkExistingMappingsStale()
    {
        var target = Path.Join(_libraryRoot, "real.mkv");
        await File.WriteAllTextAsync(target, "x");
        var link = Path.Join(_libraryRoot, "linked.mkv");
        File.CreateSymbolicLink(link, target);
        var config = new ConfigManager();
        config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.MediaLibraryDir, ConfigValue = _libraryRoot }]);
        var factory = _provider.GetRequiredService<IDbContextFactory<DavDatabaseContext>>();
        var scanner = new LibraryCatalogScanner(config, factory);
        await scanner.ReconcileOnceAsync(CancellationToken.None);

        File.Delete(link);
        config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.MediaLibraryEnabled, ConfigValue = "false" }]);
        await scanner.ReconcileOnceAsync(CancellationToken.None);

        await using var context = factory.CreateDbContext();
        Assert.Equal(LibraryLinkStatus.Valid, (await context.LinkMaps.SingleAsync()).Status);
    }

    [Fact]
    public async Task AdditionalRoots_KeepIdenticalRelativeLinkNamesDistinct()
    {
        var secondRoot = Path.Join(Path.GetTempPath(), $"nzbdav-special-{Guid.NewGuid():N}");
        Directory.CreateDirectory(secondRoot);
        try
        {
            var target = Path.Join(_libraryRoot, "real.mkv");
            await File.WriteAllTextAsync(target, "x");
            File.CreateSymbolicLink(Path.Join(_libraryRoot, "linked.mkv"), target);
            File.CreateSymbolicLink(Path.Join(secondRoot, "linked.mkv"), target);
            var config = new ConfigManager();
            config.UpdateValues([
                new ConfigItem { ConfigName = ConfigKeys.MediaLibraryDir, ConfigValue = _libraryRoot },
                new ConfigItem { ConfigName = ConfigKeys.MediaLibraryScanDirs,
                    ConfigValue = System.Text.Json.JsonSerializer.Serialize(new[] { secondRoot }) },
            ]);
            var factory = _provider.GetRequiredService<IDbContextFactory<DavDatabaseContext>>();
            var scanner = new LibraryCatalogScanner(config, factory);

            await scanner.ReconcileOnceAsync(CancellationToken.None);
            await using (var context = factory.CreateDbContext())
            {
                var paths = await context.LinkMaps.Select(x => x.LinkPath).ToListAsync();
                Assert.Equal(2, paths.Count);
                Assert.Contains("linked.mkv", paths);
                Assert.Contains(Path.Join(secondRoot, "linked.mkv"), paths);
            }

            File.Delete(Path.Join(secondRoot, "linked.mkv"));
            await scanner.ReconcileOnceAsync(CancellationToken.None);
            await using var updated = factory.CreateDbContext();
            Assert.Equal(LibraryLinkStatus.Stale,
                (await updated.LinkMaps.SingleAsync(x => x.LinkPath == Path.Join(secondRoot, "linked.mkv"))).Status);
            Assert.Equal(LibraryLinkStatus.Valid,
                (await updated.LinkMaps.SingleAsync(x => x.LinkPath == "linked.mkv")).Status);
        }
        finally
        {
            Directory.Delete(secondRoot, recursive: true);
        }
    }

    [Fact]
    public async Task OverlappingRoot_DoesNotMarkExistingMappingsStale()
    {
        var target = Path.Join(_libraryRoot, "real.mkv");
        await File.WriteAllTextAsync(target, "x");
        var link = Path.Join(_libraryRoot, "linked.mkv");
        File.CreateSymbolicLink(link, target);
        var config = new ConfigManager();
        config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.MediaLibraryDir, ConfigValue = _libraryRoot }]);
        var factory = _provider.GetRequiredService<IDbContextFactory<DavDatabaseContext>>();
        var scanner = new LibraryCatalogScanner(config, factory);
        await scanner.ReconcileOnceAsync(CancellationToken.None);

        File.Delete(link);
        Directory.CreateDirectory(Path.Join(_libraryRoot, "nested"));
        config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.MediaLibraryScanDirs,
            ConfigValue = System.Text.Json.JsonSerializer.Serialize(new[] { Path.Join(_libraryRoot, "nested") }) }]);
        await scanner.ReconcileOnceAsync(CancellationToken.None);

        await using var context = factory.CreateDbContext();
        Assert.Equal(LibraryLinkStatus.Valid, (await context.LinkMaps.SingleAsync()).Status);
        Assert.Contains("overlap", scanner.LastScanWarning);
    }

    [Fact]
    public async Task MissingAdditionalRoot_KeepsPreviousCatalog()
    {
        var target = Path.Join(_libraryRoot, "real.mkv");
        await File.WriteAllTextAsync(target, "x");
        var link = Path.Join(_libraryRoot, "linked.mkv");
        File.CreateSymbolicLink(link, target);
        var config = new ConfigManager();
        config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.MediaLibraryDir, ConfigValue = _libraryRoot }]);
        var factory = _provider.GetRequiredService<IDbContextFactory<DavDatabaseContext>>();
        var scanner = new LibraryCatalogScanner(config, factory);
        await scanner.ReconcileOnceAsync(CancellationToken.None);

        File.Delete(link);
        config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.MediaLibraryScanDirs,
            ConfigValue = System.Text.Json.JsonSerializer.Serialize(new[] { Path.Join(_libraryRoot, "missing") }) }]);
        await scanner.ReconcileOnceAsync(CancellationToken.None);

        await using var context = factory.CreateDbContext();
        Assert.Equal(LibraryLinkStatus.Valid, (await context.LinkMaps.SingleAsync()).Status);
        Assert.NotNull(scanner.LastScanWarning);
    }
}
