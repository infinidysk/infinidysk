using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Queue.FileProcessors;
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

    [Theory]
    [InlineData("disc/inner.rar")]
    [InlineData("disc/inner.r00")]
    [InlineData("disc/inner.part01.RAR")]
    [InlineData("disc\\inner.R77")]
    public void ThrowIfValidationFails_WithoutMediaAndRarInsideSevenZip_ExplainsUnsupportedNesting(
        string memberPath)
    {
        SeedFile("notes.txt");
        var result = SevenZipResult(memberPath);
        var member = Assert.Single(result.SevenZipFiles);
        var metadata = member.DavMultipartFileMeta;
        var validator = new EnsureImportableMediaValidator(_dbClient);

        var exception = Assert.Throws<NoMediaFilesFoundException>(
            () => validator.ThrowIfValidationFails([result]));

        Assert.Equal(EnsureImportableMediaValidator.NestedRarInSevenZipFailureMessage, exception.Message);
        Assert.Contains("RAR members inside 7z", exception.Message);
        Assert.True(exception.IsNonRetryableDownloadException());
        Assert.False(exception.IsRetryableDownloadException());
        Assert.True(exception.TryGetKnownErrorMessage(out var reason));
        Assert.Equal(exception.Message, reason);
        Assert.Same(member, Assert.Single(result.SevenZipFiles));
        Assert.Same(metadata, member.DavMultipartFileMeta);
        Assert.Equal(memberPath, member.PathWithinArchive);
        Assert.DoesNotContain(memberPath, exception.Message);
    }

    [Theory]
    [InlineData("feature.mkv")]
    [InlineData("track.flac")]
    public void ThrowIfValidationFails_WithMediaAndRarInsideSevenZip_DoesNotThrow(string mediaName)
    {
        SeedFile(mediaName);
        var validator = new EnsureImportableMediaValidator(_dbClient);

        var exception = Record.Exception(
            () => validator.ThrowIfValidationFails([SevenZipResult("disc/inner.rar")]));

        Assert.Null(exception);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void ThrowIfValidationFails_WithoutMediaAndNoNestedContext_KeepsGenericMessage(int caseNumber)
    {
        SeedFile("notes.txt");
        IReadOnlyList<BaseProcessor.Result>? results = caseNumber switch
        {
            0 => null,
            1 => [],
            2 => [SevenZipResult("notes.txt")],
            3 => [SevenZipResult("folder.rar/notes.txt")],
            4 => [SevenZipResult("inner.rar.txt")],
            _ => [SevenZipResult("inner.7z")],
        };
        var validator = new EnsureImportableMediaValidator(_dbClient);

        var exception = Assert.Throws<NoMediaFilesFoundException>(
            () => validator.ThrowIfValidationFails(results));

        Assert.Equal("No importable media files found.", exception.Message);
    }

    [Fact]
    public void ThrowIfValidationFails_DirectRarOutputDoesNotClaimSevenZipNesting()
    {
        SeedFile("inner.rar");
        var validator = new EnsureImportableMediaValidator(_dbClient);

        var exception = Assert.Throws<NoMediaFilesFoundException>(() =>
            validator.ThrowIfValidationFails(
            [new RarProcessor.Result { StoredFileSegments = [] }]));

        Assert.Equal("No importable media files found.", exception.Message);
    }

    [Fact]
    public void ThrowIfValidationFails_FindsNestedContextAfterUnrelatedResults()
    {
        SeedFile("notes.txt");
        var validator = new EnsureImportableMediaValidator(_dbClient);

        var exception = Assert.Throws<NoMediaFilesFoundException>(() =>
            validator.ThrowIfValidationFails(
            [
                new RarProcessor.Result { StoredFileSegments = [] },
                SevenZipResult("notes.txt"),
                SevenZipResult("disc/inner.rar"),
            ]));

        Assert.Equal(EnsureImportableMediaValidator.NestedRarInSevenZipFailureMessage, exception.Message);
    }

    [Fact]
    public void ThrowIfValidationFails_DoesNotIncludeArchiveMetadataSecrets()
    {
        SeedFile("notes.txt");
        var result = SevenZipResult("synthetic-private-member.rar");
        var member = Assert.Single(result.SevenZipFiles);
        member.DavMultipartFileMeta.ArchivePassword = "synthetic-password-not-a-real-secret";
        var validator = new EnsureImportableMediaValidator(_dbClient);

        var exception = Assert.Throws<NoMediaFilesFoundException>(() =>
            validator.ThrowIfValidationFails([result]));

        Assert.Equal(EnsureImportableMediaValidator.NestedRarInSevenZipFailureMessage, exception.Message);
        Assert.DoesNotContain("synthetic-private-member.rar", exception.Message);
        Assert.DoesNotContain("synthetic-password-not-a-real-secret", exception.Message);
        Assert.Equal("synthetic-private-member.rar", member.PathWithinArchive);
        Assert.Equal("synthetic-password-not-a-real-secret", member.DavMultipartFileMeta.ArchivePassword);
    }

    [Fact]
    public void ThrowIfValidationFails_WithMedia_DoesNotEnumerateProcessorResults()
    {
        SeedFile("feature.mkv");
        new EnsureImportableMediaValidator(_dbClient)
            .ThrowIfValidationFails(new UnexpectedEnumerationResults());
    }

    private static SevenZipProcessor.Result SevenZipResult(params string[] paths) => new()
    {
        SevenZipFiles = paths.Select(path => new SevenZipProcessor.SevenZipFile
        {
            ArchiveSetId = "set:synthetic-sevenzip",
            PathWithinArchive = path,
            ReleaseDate = DateTimeOffset.UnixEpoch,
            DavMultipartFileMeta = new DavMultipartFile.Meta(),
        }).ToList(),
    };

    private sealed class UnexpectedEnumerationResults : IReadOnlyList<BaseProcessor.Result>
    {
        public int Count => throw new InvalidOperationException("Archive results must not be inspected.");

        public BaseProcessor.Result this[int index] =>
            throw new InvalidOperationException("Archive results must not be inspected.");

        public IEnumerator<BaseProcessor.Result> GetEnumerator() =>
            throw new InvalidOperationException("Archive results must not be inspected.");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
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
