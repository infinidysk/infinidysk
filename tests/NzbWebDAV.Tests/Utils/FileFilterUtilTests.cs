using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Utils;

public class FileFilterUtilTests
{
    private const long FeatureSize = 8_000_000_000; // 8 GB main video
    private const long SampleSize = 40_000_000; // 40 MB sample (0.5% of the feature)

    [Theory]
    [InlineData("sample.mkv")]
    [InlineData("Sample.mkv")]
    [InlineData("Show.S01E01.1080p.WEB.sample.mkv")]
    [InlineData("sample-Show.S01E01.1080p.mkv")]
    [InlineData("Show.S01E01 (sample).mkv")]
    [InlineData("Show.S01E01.samples.mkv")]
    public void IsSampleFile_SmallVideoNamedSample_IsFiltered(string filename)
    {
        Assert.True(FileFilterUtil.IsSampleFile(filename, SampleSize, FeatureSize));
    }

    [Fact]
    public void IsSampleFile_LargestVideoInTheRelease_IsNeverFiltered()
    {
        Assert.False(FileFilterUtil.IsSampleFile("Free.Samples.2012.1080p.BluRay.mkv", FeatureSize, FeatureSize));
    }

    [Fact]
    public void IsSampleFile_SampleOnlyRelease_IsNeverFiltered()
    {
        Assert.False(FileFilterUtil.IsSampleFile("sample.mkv", SampleSize, SampleSize));
    }

    [Fact]
    public void IsSampleFile_LargeExtraNamedSample_IsNotFiltered()
    {
        Assert.False(FileFilterUtil.IsSampleFile("Show.sample.mkv", FeatureSize / 2, FeatureSize));
    }

    [Theory]
    [InlineData("Resampled.Audio.mkv")]
    [InlineData("Oversampling.mkv")]
    public void IsSampleFile_WordEmbeddedInALongerWord_IsNotFiltered(string filename)
    {
        Assert.False(FileFilterUtil.IsSampleFile(filename, SampleSize, FeatureSize));
    }

    [Theory]
    [InlineData("dating.naked.uk.s01e07.italian.1080p.web.h264-neurosis-sample.mkv")]
    [InlineData("sample-dating.naked.uk.s01e07.1080p.web.h264-bussy.mkv")]
    [InlineData("love.on.the.spectrum.u.s.s03e01.polish.1080p.web.h264-flame-sample.mkv")]
    public void IsSampleFile_RealWorldSampleNames_AreFiltered(string filename)
    {
        Assert.True(FileFilterUtil.IsSampleFile(filename, 75 * 1024 * 1024L, 1536 * 1024 * 1024L));
    }

    [Fact]
    public void IsSampleFile_RarPackedRelease_KeepsTheSampleUntilTheArchiveIsExpanded()
    {
        // Before extraction the sample is the only video and compares against itself.
        const long sampleSize = 75 * 1024 * 1024L;
        Assert.False(FileFilterUtil.IsSampleFile(
            "dating.naked.uk.s01e07.italian.1080p.web.h264-neurosis-sample.mkv",
            sampleSize, largestVideoFileSize: sampleSize));
    }

    [Fact]
    public void IsSampleFile_NonVideoFile_IsNotFiltered()
    {
        Assert.False(FileFilterUtil.IsSampleFile("sample.srt", 1024, FeatureSize));
    }

    [Fact]
    public void IsSampleFile_WithoutAKnownSize_IsNotFiltered()
    {
        Assert.False(FileFilterUtil.IsSampleFile("sample.mkv", null, FeatureSize));
        Assert.False(FileFilterUtil.IsSampleFile("sample.mkv", SampleSize, largestVideoFileSize: 0));
    }

    [Theory]
    [InlineData("Show.S01E01.trailer.mkv", "*trailer*", true)]
    [InlineData("Show.S01E01.mkv", "*trailer*", false)]
    [InlineData("PROOF.JPG", "proof.jpg", true)]
    [InlineData("notproof.jpg", "proof.jpg", false)]
    public void MatchesAnyGlob_MatchesTheFilenameCaseInsensitively(string filename, string glob, bool expected)
    {
        Assert.Equal(expected, FileFilterUtil.MatchesAnyGlob(filename, [glob]));
    }

    [Fact]
    public void MatchesAnyGlob_WithNoPatterns_MatchesNothing()
    {
        Assert.False(FileFilterUtil.MatchesAnyGlob("anything.mkv", Array.Empty<string>()));
    }

    [Fact]
    public void MatchesAnyGlob_IgnoresTheDirectoryPart()
    {
        Assert.False(FileFilterUtil.MatchesAnyGlob("/content/sample.release/Show.mkv", ["*sample*"]));
        Assert.True(FileFilterUtil.MatchesAnyGlob("/content/show/Show.sample.mkv", ["*sample*"]));
    }
}
