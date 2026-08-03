using SharpCompress.Common.Rar;
using Xunit;

namespace SharpCompress.Test;

/// <summary>
/// Endian and RAR v-int edge cases plus Mark()/CurrentReadByteCount semantics for the
/// consolidated <see cref="RarBlockBuffer"/>. These previously lived in
/// MarkingBinaryReaderParityTests and are retained here after the reader consolidation.
/// </summary>
public class RarBlockBufferTests : TestBase
{
    private readonly byte[] _testData;

    public RarBlockBufferTests()
    {
        _testData = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            _testData[i] = (byte)i;
        }
    }

    [Fact]
    public void Mark_Resets_ByteCount()
    {
        using var reader = RarBlockBuffer.CreateForTest(_testData);

        reader.ReadBytes(10);
        Assert.Equal(10, reader.CurrentReadByteCount);

        reader.Mark();
        Assert.Equal(0, reader.CurrentReadByteCount);

        reader.ReadBytes(5);
        Assert.Equal(5, reader.CurrentReadByteCount);
    }

    [Fact]
    public void ReadByte_Updates_ByteCount()
    {
        using var reader = RarBlockBuffer.CreateForTest(_testData);

        reader.Mark();
        reader.ReadByte();
        Assert.Equal(1, reader.CurrentReadByteCount);

        reader.ReadByte();
        Assert.Equal(2, reader.CurrentReadByteCount);
    }

    [Fact]
    public void ReadBytes_Updates_ByteCount()
    {
        using var reader = RarBlockBuffer.CreateForTest(_testData);

        reader.Mark();
        reader.ReadBytes(16);
        Assert.Equal(16, reader.CurrentReadByteCount);

        reader.ReadBytes(8);
        Assert.Equal(24, reader.CurrentReadByteCount);
    }

    [Fact]
    public void ReadUInt16_Updates_ByteCount()
    {
        using var reader = RarBlockBuffer.CreateForTest(_testData);

        reader.Mark();
        reader.ReadUInt16();
        Assert.Equal(2, reader.CurrentReadByteCount);
    }

    [Fact]
    public void ReadUInt32_Updates_ByteCount()
    {
        using var reader = RarBlockBuffer.CreateForTest(_testData);

        reader.Mark();
        reader.ReadUInt32();
        Assert.Equal(4, reader.CurrentReadByteCount);
    }

    [Fact]
    public void ReadUInt16_Is_LittleEndian()
    {
        using var reader = RarBlockBuffer.CreateForTest(new byte[] { 0x34, 0x12 });
        Assert.Equal(0x1234, reader.ReadUInt16());
    }

    [Fact]
    public void ReadUInt32_Is_LittleEndian()
    {
        using var reader = RarBlockBuffer.CreateForTest(new byte[] { 0x78, 0x56, 0x34, 0x12 });
        Assert.Equal(0x12345678u, reader.ReadUInt32());
    }

    [Fact]
    public void ReadRarVInt_Updates_ByteCount()
    {
        // 0x05 (value 5, no continuation), then 0x85 0x01 (value 5 + 128 = 133)
        var data = new byte[] { 0x05, 0x85, 0x01, 0x00 };
        using var reader = RarBlockBuffer.CreateForTest(data);

        reader.Mark();
        Assert.Equal(5ul, reader.ReadRarVInt());
        Assert.Equal(1, reader.CurrentReadByteCount);

        reader.Mark();
        Assert.Equal(133ul, reader.ReadRarVInt());
        Assert.Equal(2, reader.CurrentReadByteCount);
    }

    [Fact]
    public void MixedReads_Track_ByteCount()
    {
        using var reader = RarBlockBuffer.CreateForTest(_testData);

        reader.Mark();
        reader.ReadByte();
        reader.ReadByte();
        reader.ReadUInt16();
        reader.ReadUInt32();
        reader.ReadBytes(8);

        Assert.Equal(16, reader.CurrentReadByteCount);
    }
}
