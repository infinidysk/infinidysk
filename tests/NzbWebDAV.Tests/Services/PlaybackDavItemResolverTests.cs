using System.Text.Json;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models.Playback;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public sealed class PlaybackDavItemResolverTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), $"infinidysk-playback-{Guid.NewGuid():N}");

    public PlaybackDavItemResolverTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Resolve_MapsOnlyTheActiveTranslatedStrmPath()
    {
        var library = Path.Join(_root, "library");
        Directory.CreateDirectory(library);
        var id = Guid.NewGuid();
        var strm = Path.Join(library, "movie.strm");
        File.WriteAllText(strm, $"http://localhost:3000/view/.ids/{id}.mkv");

        var config = Config(library);
        var resolver = new PlaybackDavItemResolver(config);
        var instance = Instance("/movies", library);

        var resolved = resolver.Resolve(instance, new PlaybackObservation
        {
            NativeSessionId = "s1",
            MediaSourcePath = "/movies/movie.strm",
        });

        Assert.Equal(id, resolved);
    }

    [Fact]
    public void Resolve_DoesNotUseFilenameOrTitleFallback()
    {
        var library = Path.Join(_root, "library");
        Directory.CreateDirectory(library);
        File.WriteAllText(Path.Join(library, "Dune.mkv"), "ordinary file");

        var resolver = new PlaybackDavItemResolver(Config(library));
        var resolved = resolver.Resolve(Instance("/movies", library), new PlaybackObservation
        {
            NativeSessionId = "s1",
            Title = "Dune",
            MediaSourcePath = "/movies/Dune.mkv",
        });

        Assert.Null(resolved);
    }

    [Fact]
    public void Resolve_RejectsTranslatedPathOutsideLibraryRoot()
    {
        var library = Path.Join(_root, "library");
        var outside = Path.Join(_root, "outside");
        Directory.CreateDirectory(library);
        Directory.CreateDirectory(outside);
        var id = Guid.NewGuid();
        File.WriteAllText(Path.Join(outside, "movie.strm"), $"http://localhost/view/.ids/{id}.mkv");

        var resolver = new PlaybackDavItemResolver(Config(library));
        var resolved = resolver.Resolve(Instance("/movies", outside), new PlaybackObservation
        {
            NativeSessionId = "s1",
            MediaSourcePath = "/movies/movie.strm",
        });

        Assert.Null(resolved);
    }

    [Fact]
    public void Resolve_DirectViewIdentityNeedsNoLibraryWalk()
    {
        var id = Guid.NewGuid();
        var resolver = new PlaybackDavItemResolver(new ConfigManager());

        var resolved = resolver.Resolve(Instance("/movies", "/unused"), new PlaybackObservation
        {
            NativeSessionId = "s1",
            MediaSourcePath = $"https://server.example/view/.ids/abcde/{id}.mkv",
        });

        Assert.Equal(id, resolved);
    }

    [Fact]
    public void Resolve_RevalidatesCachedLinkBeforeReuse()
    {
        var library = Path.Join(_root, "library");
        Directory.CreateDirectory(library);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var strm = Path.Join(library, "movie.strm");
        File.WriteAllText(strm, $"http://localhost/view/.ids/{first}.mkv");

        var config = Config(library);
        var resolver = new PlaybackDavItemResolver(config);
        var instance = Instance("/movies", library);
        var observation = new PlaybackObservation { NativeSessionId = "s1", MediaSourcePath = "/movies/movie.strm" };

        Assert.Equal(first, resolver.Resolve(instance, observation));
        File.WriteAllText(strm, $"http://localhost/view/.ids/{second}.mkv");
        Assert.Equal(second, resolver.Resolve(instance, observation));
    }

    private static ConfigManager Config(string library)
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.MediaLibraryDir, ConfigValue = library },
            new ConfigItem { ConfigName = ConfigKeys.RcloneMountDir, ConfigValue = "/mnt/infinidysk" },
        ]);
        return config;
    }

    private static MediaServerInstance Instance(string remotePrefix, string localPrefix) => new()
    {
        Id = Guid.NewGuid(),
        Type = MediaServerType.Plex,
        Name = "Plex",
        BaseUrl = "http://plex.test",
        Token = "secret",
        PathMappings =
        [
            new MediaServerPathMapping
            {
                MediaServerPrefix = remotePrefix,
                InfiniDyskPrefix = localPrefix,
            },
        ],
    };

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
