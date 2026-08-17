using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Queue.PostProcessors;

namespace NzbWebDAV.Tests.Queue;

public class EnsureImportableMediaValidatorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DavDatabaseContext _context;
    private readonly DavDatabaseClient _dbClient;

    public EnsureImportableMediaValidatorTests()
    {
        _dbPath = Path.Join(Path.GetTempPath(), $"media-validator-test-{Guid.NewGuid():N}.sqlite");
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        _context = new DavDatabaseContext(options);
        _context.Database.EnsureCreated();
        _dbClient = new DavDatabaseClient(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        try { File.Delete(_dbPath); } catch (IOException) { /* ignore */ }
    }

    [Fact]
    public void ThrowIfValidationFails_WithVideoFile_DoesNotThrow()
    {
        SeedFile("Show.S01E01.mkv");

        var validator = new EnsureImportableMediaValidator(_dbClient);
        var exception = Record.Exception(validator.ThrowIfValidationFails);

        Assert.Null(exception);
    }

    [Fact]
    public void ThrowIfValidationFails_WithAudioFile_DoesNotThrow()
    {
        SeedFile("Artist.Track.flac");

        var validator = new EnsureImportableMediaValidator(_dbClient);
        var exception = Record.Exception(validator.ThrowIfValidationFails);

        Assert.Null(exception);
    }

    [Fact]
    public void ThrowIfValidationFails_WithAudioFileMp3_DoesNotThrow()
    {
        SeedFile("Artist.Track.mp3");

        var validator = new EnsureImportableMediaValidator(_dbClient);
        var exception = Record.Exception(validator.ThrowIfValidationFails);

        Assert.Null(exception);
    }

    [Fact]
    public void ThrowIfValidationFails_WithMixedVideoAndAudio_DoesNotThrow()
    {
        SeedFile("Show.S01E01.mkv");
        SeedFile("Artist.Track.flac");

        var validator = new EnsureImportableMediaValidator(_dbClient);
        var exception = Record.Exception(validator.ThrowIfValidationFails);

        Assert.Null(exception);
    }

    [Fact]
    public void ThrowIfValidationFails_WithoutMedia_ThrowsNoMediaFilesFoundException()
    {
        SeedFile("release.nfo");
        SeedFile("checksums.par2");

        var validator = new EnsureImportableMediaValidator(_dbClient);
        var exception = Record.Exception(validator.ThrowIfValidationFails);

        Assert.IsType<NoMediaFilesFoundException>(exception);
        Assert.Equal("No importable media files found.", exception?.Message);
    }

    private void SeedFile(string name)
    {
        var parent = DavItem.New(
            Guid.NewGuid(), DavItem.ContentFolder, "TestRelease", null,
            DavItem.ItemType.Directory, DavItem.ItemSubType.Directory,
            null, null, null, null);
        _context.Items.Add(parent);

        var blob = new DavNzbFile
        {
            Id = Guid.NewGuid(),
            SegmentIds = ["<seg@example.com>"],
        };
        var item = DavItem.New(
            Guid.NewGuid(), parent, name, 100,
            DavItem.ItemType.UsenetFile, DavItem.ItemSubType.NzbFile,
            null, null, null, blob.Id);
        _context.Items.Add(item);
        _context.AddBlob(blob);
    }
}
