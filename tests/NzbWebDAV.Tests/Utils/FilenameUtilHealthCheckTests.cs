using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Utils;

public class FilenameUtilHealthCheckTests
{
    [Theory]
    [InlineData("movie.mkv")]
    [InlineData("movie.mp4")]
    [InlineData("movie.avi")]
    [InlineData("movie.strm")]
    [InlineData("archive.rar")]
    [InlineData("archive.r00")]
    [InlineData("archive.7z")]
    [InlineData("archive.7z.001")]
    [InlineData("movie.mkv.001")]
    [InlineData("track.mp3")]
    [InlineData("track.flac")]
    [InlineData("track.aac")]
    [InlineData("track.mka")]
    [InlineData("track.ac3")]
    [InlineData("track.eac3")]
    [InlineData("track.dts")]
    [InlineData("track.aiff")]
    [InlineData("track.ogg")]
    [InlineData("track.opus")]
    [InlineData("track.wav")]
    [InlineData("track.wma")]
    [InlineData("track.m4a")]
    [InlineData("track.alac")]
    [InlineData("track.ape")]
    [InlineData("track.wv")]
    [InlineData("track.dsf")]
    public void IsHealthCheckCandidate_ReturnsTrue_ForMediaFiles(string filename)
    {
        Assert.True(FilenameUtil.IsHealthCheckCandidate(filename));
    }

    [Theory]
    [InlineData("cover.jpg")]
    [InlineData("cover.png")]
    [InlineData("cover.gif")]
    [InlineData("cover.bmp")]
    [InlineData("cover.tiff")]
    [InlineData("cover.webp")]
    [InlineData("info.nfo")]
    [InlineData("info.txt")]
    [InlineData("subs.srt")]
    [InlineData("subs.sub")]
    [InlineData("subs.ass")]
    [InlineData("subs.ssa")]
    [InlineData("subs.idx")]
    [InlineData("subs.vtt")]
    [InlineData("checksum.sfv")]
    [InlineData("checksum.md5")]
    [InlineData("checksum.sha1")]
    [InlineData("checksum.sha256")]
    [InlineData("repair.par2")]
    [InlineData("release.nzb")]
    [InlineData("release.srr")]
    [InlineData("release.xml")]
    [InlineData("release.log")]
    [InlineData("release.cue")]
    [InlineData("release.ffprobe")]
    [InlineData("playlist.m3u8")]
    [InlineData("doc.pdf")]
    public void IsHealthCheckCandidate_ReturnsFalse_ForNonMediaFiles(string filename)
    {
        Assert.False(FilenameUtil.IsHealthCheckCandidate(filename));
    }

    [Theory]
    [InlineData("track.mp3")]
    [InlineData("track.flac")]
    [InlineData("track.aac")]
    [InlineData("track.mka")]
    [InlineData("track.ac3")]
    [InlineData("track.eac3")]
    [InlineData("track.dts")]
    [InlineData("track.aiff")]
    [InlineData("track.ogg")]
    [InlineData("track.opus")]
    [InlineData("track.wav")]
    [InlineData("track.wma")]
    [InlineData("track.m4a")]
    [InlineData("track.alac")]
    [InlineData("track.ape")]
    [InlineData("track.wv")]
    [InlineData("track.dsf")]
    [InlineData("track.dff")]
    [InlineData("track.dsd")]
    public void IsAudioFile_ReturnsTrue_ForAudioExtensions(string filename)
    {
        Assert.True(FilenameUtil.IsAudioFile(filename));
    }

    [Theory]
    [InlineData("movie.mkv")]
    [InlineData("movie.mp4")]
    [InlineData("cover.jpg")]
    [InlineData("subs.srt")]
    [InlineData("info.nfo")]
    [InlineData("archive.rar")]
    public void IsAudioFile_ReturnsFalse_ForNonAudioExtensions(string filename)
    {
        Assert.False(FilenameUtil.IsAudioFile(filename));
    }

    [Fact]
    public void NonHealthCheckExtensions_IsDisjointFromVideoAndAudio()
    {
        var videoExtensions = typeof(FilenameUtil)
            .GetField("VideoExtensions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null) as HashSet<string>;
        var audioExtensions = typeof(FilenameUtil)
            .GetField("AudioExtensions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null) as HashSet<string>;

        Assert.NotNull(videoExtensions);
        Assert.NotNull(audioExtensions);

        foreach (var ext in FilenameUtil.NonHealthCheckExtensions)
        {
            Assert.False(videoExtensions.Contains(ext), $"Extension '{ext}' is in both NonHealthCheckExtensions and VideoExtensions");
            Assert.False(audioExtensions.Contains(ext), $"Extension '{ext}' is in both NonHealthCheckExtensions and AudioExtensions");
        }
    }

    [Fact]
    public void NonHealthCheckExtensions_IncludesSubtitleExtensions()
    {
        var subtitleExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".srt", ".ass", ".ssa", ".sub", ".idx", ".vtt",
        };

        var nonHealthSet = new HashSet<string>(FilenameUtil.NonHealthCheckExtensions, StringComparer.OrdinalIgnoreCase);
        foreach (var ext in subtitleExtensions)
        {
            Assert.Contains(ext, nonHealthSet);
        }
    }
}
