using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Utils;

public sealed class MatroskaHeaderValidatorTests
{
    [Theory]
    [InlineData("1A45DFA38F42F281044282886D6174726F736B61")] // matroska, EBMLMaxIDLength 4
    [InlineData("1A45DFA38B42F281044282847765626D")] // webm
    [InlineData("1A45DFA39242F381084282886D6174726F736B61EC8100")] // EBMLMaxSizeLength 8, Void element
    [InlineData("1A45DFA38F42F281024282886D6174726F736B61")] // EBMLMaxIDLength below 4: ffmpeg accepts
    [InlineData("1A45DFA38F42F781014282886E6F74726F736B61")] // unknown DocType: ffmpeg only warns
    [InlineData("1A45DFA38B4282886D6174726F736B61")] // no length declarations: ffmpeg defaults apply
    public void GetError_HeadersFfmpegOpens_AreAccepted(string hex)
    {
        Assert.Null(MatroskaHeaderValidator.GetError(Convert.FromHexString(hex)));
    }

    [Theory]
    [InlineData("1A45DFA38F42F281144282886D6174726F736B61", "EBMLMaxIDLength 20")]
    [InlineData("1A45DFA38F42F381094282886D6174726F736B61", "EBMLMaxSizeLength 9")]
    [InlineData("1A45DFA38F42F781024282886D6174726F736B61", "EBMLReadVersion 2")]
    public void GetError_DeclarationsFfmpegRefuses_AreRejected(string hex, string reason)
    {
        Assert.Contains(reason, MatroskaHeaderValidator.GetError(Convert.FromHexString(hex)));
    }

    [Theory]
    [InlineData("1A45DFA3")] // header size missing
    [InlineData("1A45DFA300")] // invalid header size
    [InlineData("1A45DFA3FF")] // unknown-size header
    [InlineData("1A45DFA341004282886D6174726F736B6142")] // element ID crosses the end of the prefix
    [InlineData("1A45DFA38F42F28104")] // header longer than the prefix
    public void GetError_UnparseableOrTruncatedPrefix_IsNotRejected(string hex)
    {
        Assert.Null(MatroskaHeaderValidator.GetError(Convert.FromHexString(hex)));
    }

    [Fact]
    public void GetError_RefusedDeclarationBeforeTheProbeEnds_IsStillRejected()
    {
        // Header declares more bytes than the prefix holds, but the refused field is complete.
        Assert.Contains("EBMLMaxIDLength", MatroskaHeaderValidator.GetError(Convert.FromHexString("1A45DFA3410042F28114")));
    }
}
