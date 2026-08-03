using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Compressors;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Test.Mocks;
using Xunit;

namespace SharpCompress.Test.Streams;

public class LZipStreamTests
{
    private static byte[] CreateMember(string content)
    {
        using var memory = new MemoryStream();
        using (var lzip = LZipStream.Create(memory, CompressionMode.Compress, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            lzip.Write(bytes, 0, bytes.Length);
        }
        return memory.ToArray();
    }

    private static byte[] CreateMultiMemberStream(params string[] members)
    {
        using var memory = new MemoryStream();
        foreach (var member in members)
        {
            // Each LZipStream.Create writes a complete, independent LZIP member
            // (header + compressed data + trailer). Concatenating them produces a
            // valid multimember stream.
            using var lzip = LZipStream.Create(memory, CompressionMode.Compress, leaveOpen: true);
            var bytes = Encoding.UTF8.GetBytes(member);
            lzip.Write(bytes, 0, bytes.Length);
        }
        return memory.ToArray();
    }

    private static string ReadAll(Stream stream)
    {
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    [Fact]
    public void MultiMember_Read_Seekable()
    {
        var data = CreateMultiMemberStream("hello", "world");
        using var lzip = LZipStream.Create(new MemoryStream(data), CompressionMode.Decompress);

        Assert.Equal("helloworld", ReadAll(lzip));
    }

    [Fact]
    public void MultiMember_Read_NonSeekable()
    {
        var data = CreateMultiMemberStream("hello", "world");
        using var lzip = LZipStream.Create(
            new ForwardOnlyStream(new MemoryStream(data)),
            CompressionMode.Decompress
        );

        Assert.Equal("helloworld", ReadAll(lzip));
    }

    [Fact]
    public void MultiMember_Read_ByteByByte()
    {
        var data = CreateMultiMemberStream("hello", "world");
        using var lzip = LZipStream.Create(new MemoryStream(data), CompressionMode.Decompress);

        var builder = new StringBuilder();
        int value;
        while ((value = lzip.ReadByte()) != -1)
        {
            builder.Append((char)value);
        }

        Assert.Equal("helloworld", builder.ToString());
    }

    [Fact]
    public async Task MultiMember_ReadAsync_Seekable()
    {
        var data = CreateMultiMemberStream("hello", "world");
        await using var lzip = LZipStream.Create(
            new MemoryStream(data),
            CompressionMode.Decompress
        );

        using var output = new MemoryStream();
        await lzip.CopyToAsync(output);

        Assert.Equal("helloworld", Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public async Task MultiMember_ReadAsync_NonSeekable()
    {
        var data = CreateMultiMemberStream("hello", "world");
        await using var lzip = LZipStream.Create(
            new ForwardOnlyStream(new MemoryStream(data)),
            CompressionMode.Decompress
        );

        using var output = new MemoryStream();
        await lzip.CopyToAsync(output, CancellationToken.None);

        Assert.Equal("helloworld", Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public void SingleMember_LengthAndPosition_Known()
    {
        var data = CreateMember("hello");
        using var lzip = LZipStream.Create(new MemoryStream(data), CompressionMode.Decompress);

        Assert.Equal(5, lzip.Length);
        Assert.Equal(0, lzip.Position);

        Assert.Equal("hello", ReadAll(lzip));

        Assert.Equal(5, lzip.Position);
    }

    [Fact]
    public void MultiMember_Length_NotSupported()
    {
        var data = CreateMultiMemberStream("hello", "world");
        using var lzip = LZipStream.Create(new MemoryStream(data), CompressionMode.Decompress);

        Assert.Throws<System.NotSupportedException>(() => lzip.Length);
    }

    [Fact]
    public void NonSeekable_Length_NotSupported()
    {
        var data = CreateMember("hello");
        using var lzip = LZipStream.Create(
            new ForwardOnlyStream(new MemoryStream(data)),
            CompressionMode.Decompress
        );

        // Without a seekable trailer the single-member state cannot be verified,
        // so Length is unavailable.
        Assert.Throws<System.NotSupportedException>(() => lzip.Length);
    }
}
