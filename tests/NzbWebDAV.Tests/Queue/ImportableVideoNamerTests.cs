using NzbWebDAV.Queue.FileAggregators;

namespace NzbWebDAV.Tests.Queue;

public class ImportableVideoNamerTests
{
    private const string Mount = "Movie.Release.2026";

    [Theory]
    [InlineData("movie.mkv", ".mkv", true, "movie.mkv")]
    [InlineData("movie.MKV", ".mkv", true, "movie.MKV")]
    public void Normalize_LeavesRecognizedVideoExtensionsUntouched(
        string leafName, string sniffed, bool allowBaseRename, string expected)
    {
        Assert.Equal(expected, ImportableVideoNamer.Normalize(leafName, sniffed, Mount, allowBaseRename));
    }

    [Theory]
    [InlineData("movie.xyz", null, true)]
    [InlineData("movie.xyz", null, false)]
    public void Normalize_LeavesNameUntouchedWhenNoSniffedExtension(
        string leafName, string? sniffed, bool allowBaseRename)
    {
        Assert.Equal(leafName, ImportableVideoNamer.Normalize(leafName, sniffed, Mount, allowBaseRename));
    }

    [Theory]
    [InlineData("release.mkv.001")]
    [InlineData("release.6547")]
    [InlineData("part.01")]
    public void Normalize_LeavesNumericOnlyExtensionsUntouched(string leafName)
    {
        Assert.Equal(leafName, ImportableVideoNamer.Normalize(leafName, ".mkv", Mount, allowBaseRename: true));
    }

    [Fact]
    public void Normalize_RenamesObfuscatedSingleFileArchiveUsingMountName()
    {
        var leaf = "b082fa0beaa644d3aa01045d5b8d0b36.xyz";

        Assert.Equal(
            Mount + ".mkv",
            ImportableVideoNamer.Normalize(leaf, ".mkv", Mount, allowBaseRename: true));
    }

    [Fact]
    public void Normalize_KeepsObfuscatedBaseForMultiFileArchive()
    {
        var leaf = "b082fa0beaa644d3aa01045d5b8d0b36.xyz";

        Assert.Equal(
            "b082fa0beaa644d3aa01045d5b8d0b36.mkv",
            ImportableVideoNamer.Normalize(leaf, ".mkv", Mount, allowBaseRename: false));
    }

    [Fact]
    public void Normalize_ChangesOnlyExtensionForClearBaseName()
    {
        Assert.Equal(
            "Movie.Release.2026.mkv",
            ImportableVideoNamer.Normalize("Movie.Release.2026.xyz", ".mkv", Mount, allowBaseRename: true));
    }

    [Fact]
    public void Normalize_UsesMountNameWhenLeafNameIsEmpty()
    {
        Assert.Equal(
            Mount + ".mkv",
            ImportableVideoNamer.Normalize("", ".mkv", Mount, allowBaseRename: false));
    }

    [Fact]
    public void Normalize_SanitizesInvalidCharacters()
    {
        Assert.Equal(
            "Show_ Title_.mkv",
            ImportableVideoNamer.Normalize("Show: Title?.xyz", ".mkv", Mount, allowBaseRename: true));
    }
}
