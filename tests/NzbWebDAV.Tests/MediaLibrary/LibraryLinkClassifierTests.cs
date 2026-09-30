using NzbWebDAV.MediaLibrary;

namespace NzbWebDAV.Tests.MediaLibrary;

public sealed class LibraryLinkClassifierTests
{
    [Fact]
    public void Classify_IdsSymlinkTarget_ReturnsInternalWithDavItemId()
    {
        var id = Guid.NewGuid();
        var result = LibraryLinkClassifier.Classify(
            linkPath: "/library/movies/film.mkv",
            targetText: $"/mnt/nzbdav/.ids/{id}.mkv",
            isStrm: false,
            mountDir: "/mnt/nzbdav",
            libraryRoot: "/library");

        Assert.Equal(LibraryMappingType.Internal, result.MappingType);
        Assert.Equal(id, result.DavItemId);
        Assert.Equal("movies/film.mkv", result.RelativeLinkPath);
    }

    [Fact]
    public void Classify_NonIdsTarget_ReturnsExternalWithNullDavItemId()
    {
        var result = LibraryLinkClassifier.Classify(
            linkPath: "/library/docs/old.mkv",
            targetText: "/old-nas/docs/old.mkv",
            isStrm: false,
            mountDir: "/mnt/nzbdav",
            libraryRoot: "/library");

        Assert.Equal(LibraryMappingType.External, result.MappingType);
        Assert.Null(result.DavItemId);
    }

    [Fact]
    public void Classify_EscapingLinkPath_ThrowsArgumentException()
    {
        var id = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => LibraryLinkClassifier.Classify(
            linkPath: "/other/evil.mkv",
            targetText: $"/mnt/nzbdav/.ids/{id}.mkv",
            isStrm: false,
            mountDir: "/mnt/nzbdav",
            libraryRoot: "/library"));
    }
}
