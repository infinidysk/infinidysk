using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Utils;

public class FilenameUtilTests
{
    [Theory]
    [InlineData("song.flac")]
    [InlineData("song.mp3")]
    [InlineData("song.m4a")]
    [InlineData("song.ogg")]
    [InlineData("song.opus")]
    [InlineData("song.ape")]
    [InlineData("song.wv")]
    [InlineData("song.wav")]
    [InlineData("song.aac")]
    [InlineData("song.alac")]
    [InlineData("song.dsf")]
    [InlineData("song.dff")]
    [InlineData("song.wma")]
    [InlineData("song.aiff")]
    [InlineData("song.aif")]
    [InlineData("song.m4b")]
    [InlineData("song.mka")]
    [InlineData("Song.FLAC")]
    [InlineData("Song.Mp3")]
    public void IsAudioFile_WithAudioExtension_ReturnsTrue(string filename)
    {
        Assert.True(FilenameUtil.IsAudioFile(filename));
    }

    [Theory]
    [InlineData("video.mkv")]
    [InlineData("video.mp4")]
    [InlineData("video.avi")]
    [InlineData("file.nfo")]
    [InlineData("file.par2")]
    [InlineData("file.srt")]
    [InlineData("file.jpg")]
    [InlineData("file.rar")]
    [InlineData("file.txt")]
    [InlineData("file")]
    public void IsAudioFile_WithoutAudioExtension_ReturnsFalse(string filename)
    {
        Assert.False(FilenameUtil.IsAudioFile(filename));
    }

    [Theory]
    [InlineData("video.mkv")]
    [InlineData("video.mp4")]
    [InlineData("song.flac")]
    [InlineData("song.mp3")]
    [InlineData("song.ogg")]
    public void IsMediaFile_WithVideoOrAudio_ReturnsTrue(string filename)
    {
        Assert.True(FilenameUtil.IsMediaFile(filename));
    }

    [Theory]
    [InlineData("file.nfo")]
    [InlineData("file.par2")]
    [InlineData("file.srt")]
    [InlineData("file.jpg")]
    [InlineData("file.rar")]
    [InlineData("file.txt")]
    [InlineData("file")]
    public void IsMediaFile_WithoutMediaExtension_ReturnsFalse(string filename)
    {
        Assert.False(FilenameUtil.IsMediaFile(filename));
    }

    [Theory]
    [InlineData("video.mkv")]
    [InlineData("song.flac")]
    [InlineData("song.mp3")]
    [InlineData("archive.rar")]
    [InlineData("archive.7z")]
    [InlineData("video.mkv.001")]
    public void IsImportantFileType_WithMediaOrArchive_ReturnsTrue(string filename)
    {
        Assert.True(FilenameUtil.IsImportantFileType(filename));
    }

    [Theory]
    [InlineData("file.nfo")]
    [InlineData("file.par2")]
    [InlineData("file.srt")]
    [InlineData("file.jpg")]
    [InlineData("file.txt")]
    [InlineData("file")]
    public void IsImportantFileType_WithoutImportantType_ReturnsFalse(string filename)
    {
        Assert.False(FilenameUtil.IsImportantFileType(filename));
    }
}
