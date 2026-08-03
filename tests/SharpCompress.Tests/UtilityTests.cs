using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SharpCompress.Common;
using Xunit;

namespace SharpCompress.Test;

public class UtilityTests
{
    #region ReadAtLeast / EOF semantics

    [Fact]
    public void ReadAtLeast_ByteArray_ReadsExactlyRequiredBytes()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        using var stream = new MemoryStream(data);
        var buffer = new byte[5];

        var result = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);

        Assert.Equal(5, result);
        Assert.Equal(data, buffer);
    }

    [Fact]
    public void ReadAtLeast_ByteArray_ReturnsPartialWhenNotEnoughData()
    {
        var data = new byte[] { 1, 2, 3 };
        using var stream = new MemoryStream(data);
        var buffer = new byte[5];

        var result = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);

        Assert.Equal(3, result);
    }

    [Fact]
    public void ReadExactly_EmptyStream_ThrowsEndOfStreamException()
    {
        using var stream = new MemoryStream();
        var buffer = new byte[5];

        Assert.Throws<EndOfStreamException>(() => stream.ReadExactly(buffer));
    }

    #endregion

    #region ReadByteAsync Tests

    [Fact]
    public async ValueTask ReadByteAsync_ReadsOneByte()
    {
        var data = new byte[] { 42, 1, 2, 3 };
        using var stream = new MemoryStream(data);
        using var reader = new BinaryReader(stream);

        var result = await reader.ReadByteAsync();

        Assert.Equal(42, result);
        Assert.Equal(1, stream.Position);
    }

    [Fact]
    public async ValueTask ReadByteAsync_EmptyStream_ThrowsIncompleteArchiveException()
    {
        using var stream = new MemoryStream();
        using var reader = new BinaryReader(stream);

        await Assert.ThrowsAsync<IncompleteArchiveException>(async () =>
            await reader.ReadByteAsync()
        );
    }

    [Fact]
    public async ValueTask ReadByteAsync_MultipleReads_ReadsSequentially()
    {
        var data = new byte[] { 1, 2, 3 };
        using var stream = new MemoryStream(data);
        using var reader = new BinaryReader(stream);

        var first = await reader.ReadByteAsync();
        var second = await reader.ReadByteAsync();
        var third = await reader.ReadByteAsync();

        Assert.Equal(1, first);
        Assert.Equal(2, second);
        Assert.Equal(3, third);
    }

    #endregion

    #region ReadBytesAsync Tests

    [Fact]
    public async ValueTask ReadBytesAsync_ReadsExactlyRequiredBytes()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        using var stream = new MemoryStream(data);
        using var reader = new BinaryReader(stream);

        var result = await reader.ReadBytesAsync(3);

        Assert.Equal(new byte[] { 1, 2, 3 }, result);
        Assert.Equal(3, stream.Position);
    }

    [Fact]
    public async ValueTask ReadBytesAsync_NotEnoughData_ThrowsIncompleteArchiveException()
    {
        var data = new byte[] { 1, 2, 3 };
        using var stream = new MemoryStream(data);
        using var reader = new BinaryReader(stream);

        await Assert.ThrowsAsync<IncompleteArchiveException>(async () =>
            await reader.ReadBytesAsync(5)
        );
    }

    [Fact]
    public async ValueTask ReadBytesAsync_EmptyStream_ThrowsIncompleteArchiveException()
    {
        using var stream = new MemoryStream();
        using var reader = new BinaryReader(stream);

        await Assert.ThrowsAsync<IncompleteArchiveException>(async () =>
            await reader.ReadBytesAsync(1)
        );
    }

    [Fact]
    public async ValueTask ReadBytesAsync_ZeroBytes_ReturnsEmptyArray()
    {
        var data = new byte[] { 1, 2, 3 };
        using var stream = new MemoryStream(data);
        using var reader = new BinaryReader(stream);

        var result = await reader.ReadBytesAsync(0);

        Assert.Empty(result);
        Assert.Equal(0, stream.Position);
    }

    #endregion

    #region Skip Tests

    [Fact]
    public void Skip_SeekableStream_UsesSeek()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        using var stream = new MemoryStream(data);

        stream.Skip(3);

        Assert.Equal(3, stream.Position);
    }

    [Fact]
    public void Skip_SeekableStream_SkipsCorrectAmount()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        using var stream = new MemoryStream(data);

        stream.Skip(2);
        var buffer = new byte[2];
        stream.Read(buffer);

        Assert.Equal(new byte[] { 3, 4 }, buffer);
    }

    [Fact]
    public void Skip_NonSeekableStream_SkipsCorrectAmount()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        using var seekableStream = new MemoryStream(data);
        using var nonSeekableStream = new NonSeekableStream(seekableStream);

        nonSeekableStream.Skip(2);
        var buffer = new byte[2];
        nonSeekableStream.Read(buffer);

        Assert.Equal(new byte[] { 3, 4 }, buffer);
    }

    [Fact]
    public void Skip_NonSeekableStream_SkipsBeyondStreamEnd()
    {
        var data = new byte[] { 1, 2, 3 };
        using var seekableStream = new MemoryStream(data);
        using var nonSeekableStream = new NonSeekableStream(seekableStream);

        // Should not throw, just skip what's available
        nonSeekableStream.Skip(10);

        Assert.Equal(-1, nonSeekableStream.ReadByte());
    }

    [Fact]
    public void Skip_Parameterless_SkipsEntireStream()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        using var stream = new MemoryStream(data);

        stream.Skip();

        Assert.Equal(-1, stream.ReadByte());
    }

    [Fact]
    public void Skip_Zero_DoesNotMove()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        using var stream = new MemoryStream(data);
        stream.Position = 2;

        stream.Skip(0);

        Assert.Equal(2, stream.Position);
    }

    #endregion

    #region DosDateToDateTime Tests

    [Fact]
    public void DosDateToDateTime_ValidDate_ConvertsCorrectly()
    {
        // DOS date format: year (7 bits) | month (4 bits) | day (5 bits)
        // DOS time format: hour (5 bits) | minute (6 bits) | second (5 bits, in 2-second increments)
        // This represents: 2020-01-15 10:30:20 (approximately)
        ushort dosDate = (ushort)(((2020 - 1980) << 9) | (1 << 5) | 15); // 2020-01-15
        ushort dosTime = (ushort)((10 << 11) | (30 << 5) | 10); // 10:30:20

        var result = Utility.DosDateToDateTime(dosDate, dosTime);

        Assert.Equal(2020, result.Year);
        Assert.Equal(1, result.Month);
        Assert.Equal(15, result.Day);
        Assert.Equal(10, result.Hour);
        Assert.Equal(30, result.Minute);
        Assert.Equal(20, result.Second);
    }

    [Fact]
    public void DosDateToDateTime_InvalidDate_DefaultsTo1980_01_01()
    {
        ushort dosDate = ushort.MaxValue;
        ushort dosTime = (ushort)((10 << 11) | (30 << 5) | 10);

        var result = Utility.DosDateToDateTime(dosDate, dosTime);

        Assert.Equal(1980, result.Year);
        Assert.Equal(1, result.Month);
        Assert.Equal(1, result.Day);
    }

    [Fact]
    public void DosDateToDateTime_InvalidTime_DefaultsToMidnight()
    {
        ushort dosDate = (ushort)(((2020 - 1980) << 9) | (1 << 5) | 15);
        ushort dosTime = ushort.MaxValue;

        var result = Utility.DosDateToDateTime(dosDate, dosTime);

        Assert.Equal(0, result.Hour);
        Assert.Equal(0, result.Minute);
        Assert.Equal(0, result.Second);
    }

    [Fact]
    public void DosDateToDateTime_FromUint_ConvertsCorrectly()
    {
        ushort dosDate = (ushort)(((2020 - 1980) << 9) | (6 << 5) | 20); // 2020-06-20
        ushort dosTime = (ushort)((14 << 11) | (45 << 5) | 15); // 14:45:30
        uint combined = (uint)(dosDate << 16) | dosTime;

        var result = Utility.DosDateToDateTime(combined);

        Assert.Equal(2020, result.Year);
        Assert.Equal(6, result.Month);
        Assert.Equal(20, result.Day);
        Assert.Equal(14, result.Hour);
        Assert.Equal(45, result.Minute);
    }

    [Fact]
    public void DosDateToDateTime_OutOfRangeMonth_DefaultsTo1980_01_01()
    {
        // month = 13
        ushort dosDate = (ushort)(((2020 - 1980) << 9) | (13 << 5) | 1);
        ushort dosTime = (ushort)((10 << 11) | (30 << 5) | 10);

        var result = Utility.DosDateToDateTime(dosDate, dosTime);

        Assert.Equal(new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Local), result);
    }

    [Fact]
    public void DosDateToDateTime_InvalidCalendarDay_DefaultsTo1980_01_01()
    {
        // 2021-02-30 (non-leap year)
        ushort dosDate = (ushort)(((2021 - 1980) << 9) | (2 << 5) | 30);
        ushort dosTime = (ushort)((10 << 11) | (30 << 5) | 10);

        var result = Utility.DosDateToDateTime(dosDate, dosTime);

        Assert.Equal(new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Local), result);
    }

    [Fact]
    public void DosDateToDateTime_OutOfRangeHour_DefaultsTo1980_01_01()
    {
        ushort dosDate = (ushort)(((2020 - 1980) << 9) | (1 << 5) | 15);
        // hour = 24
        ushort dosTime = (ushort)((24 << 11) | (0 << 5) | 0);

        var result = Utility.DosDateToDateTime(dosDate, dosTime);

        Assert.Equal(new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Local), result);
    }

    #endregion

    #region DateTimeToDosTime Tests

    [Fact]
    public void DateTimeToDosTime_ValidDateTime_ConvertsCorrectly()
    {
        //always do local time
        var dt = new DateTime(2020, 6, 15, 14, 30, 20, DateTimeKind.Local);

        var result = Utility.DateTimeToDosTime(dt);

        // Verify we can convert back
        var reversed = Utility.DosDateToDateTime(result);
        Assert.Equal(2020, reversed.Year);
        Assert.Equal(6, reversed.Month);
        Assert.Equal(15, reversed.Day);
        Assert.Equal(14, reversed.Hour);
        Assert.Equal(30, reversed.Minute);
        // Seconds are rounded down to nearest even number in DOS format
        Assert.True(reversed.Second == 20 || reversed.Second == 18);
    }

    [Fact]
    public void DateTimeToDosTime_NullDateTime_ReturnsZero()
    {
        DateTime? dt = null;

        var result = Utility.DateTimeToDosTime(dt);

        Assert.Equal(0u, result);
    }

    #endregion

    #region UnixTimeToDateTime Tests

    [Fact]
    public void UnixTimeToDateTime_Zero_Returns1970_01_01()
    {
        var result = Utility.UnixTimeToDateTime(0);

        Assert.Equal(1970, result.Year);
        Assert.Equal(1, result.Month);
        Assert.Equal(1, result.Day);
        Assert.Equal(0, result.Hour);
        Assert.Equal(0, result.Minute);
        Assert.Equal(0, result.Second);
    }

    [Fact]
    public void UnixTimeToDateTime_ValidTimestamp_ConvertsCorrectly()
    {
        // January 1, 2000 00:00:00 UTC is 946684800 seconds after epoch
        var result = Utility.UnixTimeToDateTime(946684800);

        Assert.Equal(2000, result.Year);
        Assert.Equal(1, result.Month);
        Assert.Equal(1, result.Day);
    }

    [Fact]
    public void UnixTimeToDateTime_NegativeTimestamp_ReturnsBeforeEpoch()
    {
        // -86400 is one day before epoch
        var result = Utility.UnixTimeToDateTime(-86400);

        Assert.Equal(1969, result.Year);
        Assert.Equal(12, result.Month);
        Assert.Equal(31, result.Day);
    }

    #endregion

    #region TransferTo Tests

    [Fact]
    public void TransferTo_WithMaxLength_TransfersCorrectAmount()
    {
        var sourceData = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        using var source = new MemoryStream(sourceData);
        using var destination = new MemoryStream();

        var transferred = source.TransferTo(destination, 5, null);

        Assert.Equal(5, transferred);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, destination.ToArray());
    }

    [Fact]
    public void TransferTo_SourceSmallerThanMax_TransfersAll()
    {
        var sourceData = new byte[] { 1, 2, 3 };
        using var source = new MemoryStream(sourceData);
        using var destination = new MemoryStream();

        var transferred = source.TransferTo(destination, 100, null);

        Assert.Equal(3, transferred);
        Assert.Equal(sourceData, destination.ToArray());
    }

    [Fact]
    public void TransferTo_EmptySource_TransfersNothing()
    {
        using var source = new MemoryStream();
        using var destination = new MemoryStream();

        var transferred = source.TransferTo(destination, 100, null);

        Assert.Equal(0, transferred);
        Assert.Empty(destination.ToArray());
    }

    #endregion

    #region ReverseEndianness Tests

    [Fact]
    public void ReverseEndianness_SimpleValue_SwapsEndianness()
    {
        uint value = 0x12345678;

        var result = BinaryPrimitives.ReverseEndianness(value);

        Assert.Equal(0x78563412u, result);
    }

    [Fact]
    public void ReverseEndianness_Zero_ReturnsZero()
    {
        var result = BinaryPrimitives.ReverseEndianness(0u);

        Assert.Equal(0u, result);
    }

    [Fact]
    public void ReverseEndianness_MaxValue_SwapsCorrectly()
    {
        var result = BinaryPrimitives.ReverseEndianness(uint.MaxValue);

        Assert.Equal(uint.MaxValue, result);
    }

    [Fact]
    public void ReverseEndianness_Involution_SwappingTwiceReturnsOriginal()
    {
        uint value = 0x12345678;

        var result = BinaryPrimitives.ReverseEndianness(BinaryPrimitives.ReverseEndianness(value));

        Assert.Equal(value, result);
    }

    #endregion

    #region SetLittleUInt32 Tests

    [Fact]
    public void SetLittleUInt32_InsertsValueCorrectly()
    {
        byte[] buffer = new byte[10];
        uint value = 0x12345678;

        Utility.SetLittleUInt32(buffer, value, 2);

        Assert.Equal(0x78, buffer[2]);
        Assert.Equal(0x56, buffer[3]);
        Assert.Equal(0x34, buffer[4]);
        Assert.Equal(0x12, buffer[5]);
    }

    [Fact]
    public void SetLittleUInt32_AtOffset_InsertsBehindOffset()
    {
        byte[] buffer = new byte[10];
        uint value = 0xDEADBEEF;

        Utility.SetLittleUInt32(buffer, value, 5);

        Assert.Equal(0xEF, buffer[5]);
        Assert.Equal(0xBE, buffer[6]);
        Assert.Equal(0xAD, buffer[7]);
        Assert.Equal(0xDE, buffer[8]);
    }

    #endregion

    #region SetBigUInt32 Tests

    [Fact]
    public void SetBigUInt32_InsertsValueCorrectly()
    {
        byte[] buffer = new byte[10];
        uint value = 0x12345678;

        Utility.SetBigUInt32(buffer, value, 2);

        Assert.Equal(0x12, buffer[2]);
        Assert.Equal(0x34, buffer[3]);
        Assert.Equal(0x56, buffer[4]);
        Assert.Equal(0x78, buffer[5]);
    }

    [Fact]
    public void SetBigUInt32_AtOffset_InsertsBehindOffset()
    {
        byte[] buffer = new byte[10];
        uint value = 0xDEADBEEF;

        Utility.SetBigUInt32(buffer, value, 5);

        Assert.Equal(0xDE, buffer[5]);
        Assert.Equal(0xAD, buffer[6]);
        Assert.Equal(0xBE, buffer[7]);
        Assert.Equal(0xEF, buffer[8]);
    }

    #endregion

    #region ReplaceInvalidFileNameChars Tests

#if WINDOWS
    [Theory]
    [InlineData("valid_filename.txt", "valid_filename.txt")]
    [InlineData("file<name>test.txt", "file_name_test.txt")]
    [InlineData("<>:\"|?*", "_______")]
    public void ReplaceInvalidFileNameChars_Windows(string fileName, string expected)
    {
        var result = Utility.ReplaceInvalidFileNameChars(fileName);

        Assert.Equal(expected, result);
    }

#else
    [Theory]
    [InlineData("valid_filename.txt", "valid_filename.txt")]
    [InlineData("file<name>test.txt", "file<name>test.txt")]
    [InlineData("<>:\"|?*", "<>:\"|?*")]
    public void ReplaceInvalidFileNameChars_NonWindows(string fileName, string expected)
    {
        var result = Utility.ReplaceInvalidFileNameChars(fileName);

        Assert.Equal(expected, result);
    }
#endif

    #endregion

    #region TrimNulls Tests

    [Fact]
    public void TrimNulls_StringWithNulls_ReplacesAndTrims()
    {
        var input = "  hello\0world\0  ";

        var result = Utility.TrimNulls(input);

        Assert.Equal("hello world", result);
    }

    [Fact]
    public void TrimNulls_StringWithoutNulls_TrimsWhitespace()
    {
        var input = "  hello world  ";

        var result = Utility.TrimNulls(input);

        Assert.Equal("hello world", result);
    }

    [Fact]
    public void TrimNulls_OnlyNulls_ReturnsEmpty()
    {
        var input = "\0\0\0";

        var result = Utility.TrimNulls(input);

        Assert.Empty(result);
    }

    #endregion
}

/// <summary>
/// Helper class for testing non-seekable streams
/// </summary>
internal class NonSeekableStream : Stream
{
    private readonly Stream _inner;

    public NonSeekableStream(Stream inner)
    {
        _inner = inner;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false; // Force non-seekable
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set => throw new NotSupportedException("Stream is not seekable");
    }

    public override void Flush() => _inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) =>
        _inner.Read(buffer, offset, count);

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("Stream is not seekable");

    public override void SetLength(long value) =>
        throw new NotSupportedException("Stream is not seekable");

    public override void Write(byte[] buffer, int offset, int count) =>
        _inner.Write(buffer, offset, count);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
