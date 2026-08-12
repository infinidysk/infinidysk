using NzbWebDAV.Exceptions;
using NzbWebDAV.Models;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Utils;

public class UtilityTests
{
    [Theory]
    [InlineData("movie.rar", true, false, false)]
    [InlineData("movie.r12", true, false, false)]
    [InlineData("movie.7z.003", false, true, false)]
    [InlineData("movie.mkv.001", false, false, true)]
    [InlineData("movie.MP4.002", false, false, true)]
    [InlineData("movie.avi.010", false, false, true)]
    [InlineData("movie.mkv", false, false, false)]
    [InlineData("movie.mkv.", false, false, false)]
    [InlineData("movie.rar.001", false, false, false)]
    [InlineData("notes.pdf.001", false, false, false)]
    [InlineData("movie.mkv.999999999999", false, false, false)]
    public void FilenameClassifiers_RecognizeArchiveConventions(
        string filename, bool rar, bool sevenZip, bool splitVideo)
    {
        Assert.Equal(rar, FilenameUtil.IsRarFile(filename));
        Assert.Equal(sevenZip, FilenameUtil.Is7zFile(filename));
        Assert.Equal(splitVideo, FilenameUtil.IsSplitVideoFile(filename));
    }

    [Theory]
    [InlineData("movie.mkv.001", "movie.mkv")]
    [InlineData("EP01.MKV.002", "EP01.MKV")]
    [InlineData("clip.mp4.010", "clip.mp4")]
    [InlineData("movie.mkv", null)]
    [InlineData("movie.mkv.", null)]
    [InlineData("movie.rar.001", null)]
    [InlineData("movie.mkv.999999999999", null)]
    [InlineData("notes.pdf.001", null)]
    [InlineData(null, null)]
    public void GetSplitVideoBaseName_StripsNumericSuffixForVideoFiles(string? filename, string? expected)
    {
        Assert.Equal(expected, FilenameUtil.GetSplitVideoBaseName(filename));
    }

    [Theory]
    [InlineData("movie.mkv.001", 1)]
    [InlineData("clip.mp4.010", 10)]
    [InlineData("movie.mkv", null)]
    [InlineData("movie.mkv.999999999999", null)]
    [InlineData("notes.pdf.001", null)]
    [InlineData(null, null)]
    public void GetSplitVideoPartNumber_ParsesNumericSuffix(string? filename, int? expected)
    {
        Assert.Equal(expected, FilenameUtil.GetSplitVideoPartNumber(filename));
    }

    [Theory]
    [InlineData("archive.part03.rar", 3)]
    [InlineData("archive.r07", 100007)]
    [InlineData("x.rar", -1)]
    [InlineData("X.RAR", -1)]
    public void GetRarPartOrdinal_ParsesKnownRarSuffixes(string filename, int expected)
    {
        Assert.Equal(expected, FilenameUtil.GetRarPartOrdinal(filename));
    }

    [Theory]
    [InlineData("x.mkv")]
    [InlineData("")]
    [InlineData(null)]
    public void GetRarPartOrdinal_ReturnsNullForNonRarNames(string? filename)
    {
        Assert.Null(FilenameUtil.GetRarPartOrdinal(filename));
    }

    [Theory]
    [InlineData("Movie {{secret}}.nzb", "Movie", "secret")]
    [InlineData("Movie password=secret.nzb", "Movie", "secret")]
    [InlineData("Movie.nzb", "Movie", null)]
    public void FilenamePasswordHelpers_ParseJobNameAndPassword(
        string filename, string expectedName, string? expectedPassword)
    {
        Assert.Equal(expectedName, FilenameUtil.GetJobName(filename));
        Assert.Equal(expectedPassword, FilenameUtil.GetNzbPassword(filename));
    }

    [Theory]
    [InlineData("b082fa0beaa644d3aa01045d5b8d0b36.mkv", true)]
    [InlineData("Great.Movie.Release.2026.mkv", false)]
    [InlineData("This is a normal download.mkv", false)]
    public void ObfuscationDetection_ClassifiesRepresentativeNames(
        string filename, bool expected)
    {
        Assert.Equal(expected, ObfuscationUtil.IsProbablyObfuscated(filename));
    }

    [Fact]
    public void PathHelpers_ReturnParentsAndReplaceExtension()
    {
        Assert.Equal(
            new[] { "one", Path.Join("one", "two") },
            PathUtil.GetAllParentDirectories(Path.Join("one", "two", "file.bin")));
        Assert.Equal(
            Path.Join("one", "two", "file.strm"),
            PathUtil.ReplaceExtension(Path.Join("one", "two", "file.mkv"), ".strm"));
    }

    [Theory]
    [InlineData("file.mkv", ".strm", "file.strm")]
    [InlineData("file.mkv", "strm", "file.strm")]
    [InlineData("file.mkv", "", "file")]
    [InlineData("file.mkv", ".", "file")]
    [InlineData("file.mkv", "   ", "file")]
    [InlineData("file.mkv", null, "file")]
    public void ReplaceExtension_HandlesEmptyAndNormalExtensions(
        string path, string? newExtension, string expectedFilename)
    {
        Assert.Equal(expectedFilename, Path.GetFileName(PathUtil.ReplaceExtension(path, newExtension)));
    }

    [Fact]
    public void PasswordHash_RequiresMatchingPasswordAndSalt()
    {
        var hash = PasswordUtil.Hash("secret", "account");

        Assert.True(PasswordUtil.Verify(hash, "secret", "account"));
        Assert.False(PasswordUtil.Verify(hash, "wrong", "account"));
        Assert.False(PasswordUtil.Verify(hash, "secret", "different"));
    }

    [Fact]
    public void PasswordVerify_CacheHitsPreserveCorrectness()
    {
        var hash = PasswordUtil.Hash("secret", "account");

        Assert.True(PasswordUtil.Verify(hash, "secret", "account"));
        Assert.True(PasswordUtil.Verify(hash, "secret", "account"));
        Assert.False(PasswordUtil.Verify(hash, "wrong", "account"));
        Assert.False(PasswordUtil.Verify(hash, "wrong", "account"));
    }

    [Fact]
    public void PasswordVerifyDummy_DoesNotAuthenticate()
    {
        // Dummy verify must complete without throwing and must not imply success.
        PasswordUtil.VerifyDummy("any-password");
        PasswordUtil.VerifyDummy("any-password", "salt");
    }

    [Fact]
    public void InterpolationSearch_FindsIrregularByteRange()
    {
        LongRange[] ranges =
        [
            new(0, 10),
            new(10, 25),
            new(25, 60),
            new(60, 100)
        ];

        var result = InterpolationSearch.Find(
            42,
            new LongRange(0, ranges.Length),
            new LongRange(0, 100),
            index => ranges[index]);

        Assert.Equal(2, result.FoundIndex);
        Assert.Equal(new LongRange(25, 60), result.FoundByteRange);
    }

    [Fact]
    public void InterpolationSearch_RejectsPositionOutsideSearchRange()
    {
        Assert.Throws<SeekPositionNotFoundException>(() =>
            InterpolationSearch.Find(
                10,
                new LongRange(0, 1),
                new LongRange(0, 10),
                _ => new LongRange(0, 10)));
    }
}
