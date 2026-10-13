using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Utils;

public sealed class MatroskaHeaderValidatorTests
{
    [Theory]
    [InlineData("1A45DFA38F42F281044282886D6174726F736B61")]
    [InlineData("1A45DFA38B42F281044282847765626D")]
    [InlineData("1A45DFA39142F2810442828A6D6174726F736B610000")]
    [InlineData("1A45DFA39242F381084282886D6174726F736B61EC8100")]
    public void GetError_ValidHeadersAndUnknownElements_AreAccepted(string hex)
    {
        var bytes = Convert.FromHexString(hex);
        Assert.Null(MatroskaHeaderValidator.GetError(bytes, bytes.Length + 100));
    }

    [Theory]
    [InlineData("1A45DFA38F42F281144282886D6174726F736B61", "EBMLMaxIDLength")]
    [InlineData("1A45DFA38F42F381094282886D6174726F736B61", "EBMLMaxSizeLength")]
    [InlineData("1A45DFA38F42F381004282886D6174726F736B61", "EBMLMaxSizeLength")]
    [InlineData("1A45DFA38F42F781024282886D6174726F736B61", "EBMLReadVersion")]
    [InlineData("1A45DFA3FF", "extends beyond")]
    [InlineData("1A45DFA300", "header size")]
    [InlineData("1A45DFA38F42F281044282896D6174726F736B61", "extends beyond")]
    [InlineData("1A45DFA38F42F281044282886E6F74726F736B61", "document type")]
    public void GetError_InvalidHeader_IsRejected(string hex, string reason)
    {
        var bytes = Convert.FromHexString(hex);
        Assert.Contains(reason, MatroskaHeaderValidator.GetError(bytes, bytes.Length + 100));
    }

    [Fact]
    public void GetError_HeaderPastEndOfFile_IsRejected()
    {
        var bytes = Convert.FromHexString("1A45DFA38F42F281044282886D6174726F736B61");
        Assert.Contains("extends beyond", MatroskaHeaderValidator.GetError(bytes, 10));
    }

    [Fact]
    public void GetError_ValidHeaderLongerThanProbe_IsNotRejectedAsTruncated()
    {
        var bytes = Convert.FromHexString("1A45DFA341004282886D6174726F736B61EC40F0");
        Assert.Null(MatroskaHeaderValidator.GetError(bytes, 1000));
    }

    [Theory]
    [InlineData("1A45DFA341004282886D6174726F736B6142")]
    [InlineData("1A45DFA341004282886D6174726F736B61EC40")]
    public void GetError_ElementEncodingCrossesProbeBoundary_IsNotRejected(string hex)
    {
        var bytes = Convert.FromHexString(hex);
        Assert.Null(MatroskaHeaderValidator.GetError(bytes, 1000));
    }
}
