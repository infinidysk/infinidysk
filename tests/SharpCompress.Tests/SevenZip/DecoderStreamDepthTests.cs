using SharpCompress.Common;
using SharpCompress.Compressors.LZMA;
using Xunit;

namespace SharpCompress.Test.SevenZip;

public class DecoderStreamDepthTests
{
    [Fact]
    public void EnsureCoderChainDepth_AllowsDepthAtLimit()
    {
        var exception = Record.Exception(() =>
            DecoderStreamHelper.EnsureCoderChainDepth(DecoderStreamHelper.MaxCoderChainDepth)
        );
        Assert.Null(exception);
    }

    [Fact]
    public void EnsureCoderChainDepth_ThrowsInvalidFormatException_WhenDepthExceeded()
    {
        var exception = Assert.Throws<InvalidFormatException>(() =>
            DecoderStreamHelper.EnsureCoderChainDepth(DecoderStreamHelper.MaxCoderChainDepth + 1)
        );

        Assert.Contains("maximum depth", exception.Message);
    }
}
