using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue.PostProcessors;

namespace NzbWebDAV.Tests.Queue;

public class RenameSingleVideoPostProcessorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DavDatabaseContext _context;
    private readonly DavDatabaseClient _dbClient;
    private readonly ConfigManager _config;
    private readonly Guid _historyItemId = Guid.NewGuid();

    public RenameSingleVideoPostProcessorTests()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"rename-single-video-{Guid.NewGuid():N}.sqlite");
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        _context = new DavDatabaseContext(options);
        _context.Database.EnsureCreated();
        _dbClient = new DavDatabaseClient(_context);
        _config = new ConfigManager();
    }

    public void Dispose()
    {
        _context.Dispose();
        try { File.Delete(_dbPath); } catch (IOException) { /* ignore */ }
    }

    [Fact]
    public void DefaultMissingKey_RenamesHashNamedVideoToRelease()
    {
        var mount = SeedDirectory("Release.Name.2026");
        var video = SeedNzbFile(mount, "b082fa0beaa644d3aa01045d5b8d0b36.mkv", 1_000_000_000);

        new RenameSingleVideoPostProcessor(_config, _dbClient).RenameToReleaseName(mount);

        Assert.Equal("Release.Name.2026.mkv", video.Name);
        Assert.Equal(Path.Join(mount.Path, "Release.Name.2026.mkv"), video.Path);
        Assert.Equal(mount.Id, video.ParentId);
        Assert.Equal(1_000_000_000, video.FileSize);
        Assert.NotEqual(Guid.Empty, video.FileBlobId);
    }

    [Fact]
    public void ExplicitFalse_LeavesOriginalName()
    {
        SetEnabled("false");
        var mount = SeedDirectory("Release.Name.2026");
        var video = SeedNzbFile(mount, "b082fa0beaa644d3aa01045d5b8d0b36.mkv");

        new RenameSingleVideoPostProcessor(_config, _dbClient).RenameToReleaseName(mount);

        Assert.Equal("b082fa0beaa644d3aa01045d5b8d0b36.mkv", video.Name);
    }

    [Fact]
    public void PreservesExtensionCase_AndDoesNotDoubleAppend()
    {
        var mount = SeedDirectory("Release.Name.2026");
        var video = SeedNzbFile(mount, "clip.MP4");

        new RenameSingleVideoPostProcessor(_config, _dbClient).RenameToReleaseName(mount);

        Assert.Equal("Release.Name.2026.MP4", video.Name);

        var mountWithExt = SeedDirectory("Already.Named.mkv");
        var already = SeedNzbFile(mountWithExt, "Already.Named.mkv");
        new RenameSingleVideoPostProcessor(_config, _dbClient).RenameToReleaseName(mountWithExt);
        Assert.Equal("Already.Named.mkv", already.Name);
    }

    [Fact]
    public void FolderExtensionDifferentCasing_UsesSourceExtension()
    {
        var mount = SeedDirectory("Release.MKV");
        var video = SeedNzbFile(mount, "video.mkv");

        new RenameSingleVideoPostProcessor(_config, _dbClient).RenameToReleaseName(mount);

        Assert.Equal("Release.mkv", video.Name);
    }

    [Fact]
    public void NumericExtension_IsNotRenamed()
    {
        var mount = SeedDirectory("Release.Name.2026");
        var split = SeedNzbFile(mount, "release.001");

        new RenameSingleVideoPostProcessor(_config, _dbClient).RenameToReleaseName(mount);

        Assert.Equal("release.001", split.Name);
    }

    [Fact]
    public void CompanionsDoNotBlockRename_TwoVideosNeverRename()
    {
        var mount = SeedDirectory("Release.Name.2026");
        var video = SeedNzbFile(mount, "hash.mkv");
        SeedNzbFile(mount, "release.srt");
        SeedNzbFile(mount, "release.nfo");

        new RenameSingleVideoPostProcessor(_config, _dbClient).RenameToReleaseName(mount);
        Assert.Equal("Release.Name.2026.mkv", video.Name);

        var season = SeedDirectory("Season.Pack.2026");
        var ep1 = SeedNzbFile(season, "e01.mkv");
        var ep2 = SeedNzbFile(season, "e02.mkv");
        new RenameSingleVideoPostProcessor(_config, _dbClient).RenameToReleaseName(season);
        Assert.Equal("e01.mkv", ep1.Name);
        Assert.Equal("e02.mkv", ep2.Name);
    }

    [Fact]
    public void TrackedSiblingCollision_KeepsOriginalName()
    {
        var mount = SeedDirectory("Release.Name.2026");
        var video = SeedNzbFile(mount, "hash.mkv");
        SeedNzbFile(mount, "Release.Name.2026.mkv");

        new RenameSingleVideoPostProcessor(_config, _dbClient).RenameToReleaseName(mount);

        Assert.Equal("hash.mkv", video.Name);
    }

    [Fact]
    public void PersistedSiblingCollision_KeepsOriginalName()
    {
        var mount = SeedDirectory("Release.Name.2026");
        var sibling = SeedNzbFile(mount, "Release.Name.2026.mkv");
        _context.SaveChanges();
        Assert.Equal(EntityState.Unchanged, _context.Entry(sibling).State);

        // Drop the sibling from the tracker so collision is only visible via the DB query.
        _context.Entry(sibling).State = EntityState.Detached;
        Assert.Equal(EntityState.Detached, _context.Entry(sibling).State);

        var video = SeedNzbFile(mount, "hash.mkv");
        new RenameSingleVideoPostProcessor(_config, _dbClient).RenameToReleaseName(mount);

        Assert.Equal("hash.mkv", video.Name);
    }

    [Fact]
    public void UnrelatedHistoryItem_DoesNotAffectCount()
    {
        var mount = SeedDirectory("Release.Name.2026");
        var video = SeedNzbFile(mount, "hash.mkv");
        var otherMount = SeedDirectory("Other.Release", Guid.NewGuid());
        SeedNzbFile(otherMount, "other.mkv", historyItemId: otherMount.HistoryItemId);

        new RenameSingleVideoPostProcessor(_config, _dbClient).RenameToReleaseName(mount);

        Assert.Equal("Release.Name.2026.mkv", video.Name);
        Assert.Equal(mount.Id, video.ParentId);
        Assert.NotNull(video.FileBlobId);
    }

    private void SetEnabled(string value)
    {
        _config.UpdateValues(
        [
            new()
            {
                ConfigName = ConfigKeys.ApiRenameSingleVideoToRelease,
                ConfigValue = value,
            },
        ]);
    }

    private DavItem SeedDirectory(string name, Guid? historyItemId = null)
    {
        var item = DavItem.New(
            Guid.NewGuid(), DavItem.ContentFolder, name, null,
            DavItem.ItemType.Directory, DavItem.ItemSubType.Directory,
            null, null, historyItemId ?? _historyItemId, null);
        _context.Items.Add(item);
        return item;
    }

    private DavItem SeedNzbFile(
        DavItem parent,
        string name,
        long fileSize = 100,
        Guid? historyItemId = null)
    {
        var blob = new DavNzbFile
        {
            Id = Guid.NewGuid(),
            SegmentIds = ["<seg@example.com>"],
        };
        var item = DavItem.New(
            Guid.NewGuid(), parent, name, fileSize,
            DavItem.ItemType.UsenetFile, DavItem.ItemSubType.NzbFile,
            null, null, historyItemId ?? parent.HistoryItemId, blob.Id);
        _context.Items.Add(item);
        _context.AddBlob(blob);
        return item;
    }
}
