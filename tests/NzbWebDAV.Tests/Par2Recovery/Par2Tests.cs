using System.Runtime.InteropServices;
using System.Text;
using NzbWebDAV.Par2Recovery;
using NzbWebDAV.Par2Recovery.Packets;

namespace NzbWebDAV.Tests.Par2Recovery;

public class Par2Tests
{
    [Fact]
    public void HasPar2MagicBytes_RecognizesPacketHeader()
    {
        var bytes = new byte[128];
        Encoding.ASCII.GetBytes("PAR2\0PKT").CopyTo(bytes, 0);

        Assert.True(Par2.HasPar2MagicBytes(bytes));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not par2")]
    public void HasPar2MagicBytes_RejectsInvalidInput(string content)
    {
        Assert.False(Par2.HasPar2MagicBytes(Encoding.ASCII.GetBytes(content)));
    }

    [Fact]
    public async Task ReadFileDescriptions_StopsAtInvalidPacket()
    {
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes("not a packet"));

        var descriptions = new List<object>();
        await foreach (var description in Par2.ReadFileDescriptions(stream))
            descriptions.Add(description);

        Assert.Empty(descriptions);
    }

    [Fact]
    public async Task ReadFileDescriptions_PrefersUniFileNOverFileDescWhenFileIdsMatch()
    {
        var fileId = Enumerable.Range(10, 16).Select(i => (byte)i).ToArray();
        var asciiName = "obfuscated.mkv";
        var unicodeName = "한국어_예능.mkv";

        await using var stream = new MemoryStream();
        WritePacket(stream, FileDesc.PacketType, BuildFileDescBody(fileId, asciiName));
        WritePacket(stream, UniFileN.PacketType, BuildUniFileNBody(fileId, unicodeName));
        stream.Position = 0;

        var descriptions = new List<FileDesc>();
        await foreach (var description in Par2.ReadFileDescriptions(stream))
            descriptions.Add(description);

        var desc = Assert.Single(descriptions);
        Assert.Equal(unicodeName, desc.FileName);
        Assert.Equal(fileId, desc.FileID);
    }

    [Fact]
    public async Task ReadFileDescriptions_PrefersUniFileNWhenItAppearsBeforeFileDesc()
    {
        var fileId = Enumerable.Repeat((byte)0x42, 16).ToArray();
        var asciiName = "ascii.mkv";
        var unicodeName = "日本語テスト.mkv";

        await using var stream = new MemoryStream();
        WritePacket(stream, UniFileN.PacketType, BuildUniFileNBody(fileId, unicodeName));
        WritePacket(stream, FileDesc.PacketType, BuildFileDescBody(fileId, asciiName));
        stream.Position = 0;

        var descriptions = new List<FileDesc>();
        await foreach (var description in Par2.ReadFileDescriptions(stream))
            descriptions.Add(description);

        Assert.Equal(unicodeName, Assert.Single(descriptions).FileName);
    }

    [Fact]
    public async Task ReadFileDescriptions_KeepsFileDescNameWhenNoMatchingUniFileN()
    {
        var fileId = Enumerable.Repeat((byte)0x11, 16).ToArray();
        var otherFileId = Enumerable.Repeat((byte)0x22, 16).ToArray();

        await using var stream = new MemoryStream();
        WritePacket(stream, FileDesc.PacketType, BuildFileDescBody(fileId, "keep-me.mkv"));
        WritePacket(stream, UniFileN.PacketType, BuildUniFileNBody(otherFileId, "다른이름.mkv"));
        stream.Position = 0;

        var descriptions = new List<FileDesc>();
        await foreach (var description in Par2.ReadFileDescriptions(stream))
            descriptions.Add(description);

        Assert.Equal("keep-me.mkv", Assert.Single(descriptions).FileName);
    }

    [Fact]
    public async Task ReadFileDescriptions_SkipsRecoverySliceBodiesWithoutReadingThem()
    {
        var fileId = Enumerable.Repeat((byte)0x33, 16).ToArray();
        var hugeRecoveryBody = new byte[5 * 1024 * 1024]; // 5 MiB RecvSlic payload
        Array.Fill(hugeRecoveryBody, (byte)0xAB);

        await using var stream = new ReadTrackingMemoryStream();
        WritePacket(stream, FileDesc.PacketType, BuildFileDescBody(fileId, "episode.mkv"));
        WritePacket(stream, RecvSlic.PacketType, hugeRecoveryBody);
        WritePacket(stream, FileDesc.PacketType, BuildFileDescBody(
            Enumerable.Repeat((byte)0x44, 16).ToArray(), "trailing.mkv"));
        stream.Position = 0;

        var descriptions = new List<FileDesc>();
        await foreach (var description in Par2.ReadFileDescriptions(stream))
            descriptions.Add(description);

        Assert.Equal(2, descriptions.Count);
        // The 5 MiB RecvSlic body must be skipped via Seek, not read into memory.
        Assert.True(stream.TotalBytesRead < hugeRecoveryBody.Length,
            $"Expected recovery body to be seeked past, but {stream.TotalBytesRead} bytes were read");
    }

    [Fact]
    public async Task ReadFileDescriptions_StopAtRecoverySlice_EndsAtFirstRecoverySlice()
    {
        var fileId = Enumerable.Repeat((byte)0x55, 16).ToArray();

        await using var stream = new MemoryStream();
        WritePacket(stream, FileDesc.PacketType, BuildFileDescBody(fileId, "episode.mkv"));
        WritePacket(stream, RecvSlic.PacketType, new byte[64]);
        WritePacket(stream, FileDesc.PacketType, BuildFileDescBody(
            Enumerable.Repeat((byte)0x66, 16).ToArray(), "should-not-be-read.mkv"));
        stream.Position = 0;

        var descriptions = new List<FileDesc>();
        await foreach (var description in Par2.ReadFileDescriptions(stream, stopAtRecoverySlice: true))
            descriptions.Add(description);

        Assert.Equal("episode.mkv", Assert.Single(descriptions).FileName);
    }

    private sealed class ReadTrackingMemoryStream : MemoryStream
    {
        public long TotalBytesRead { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            TotalBytesRead += read;
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            TotalBytesRead += read;
            return read;
        }
    }

    private static byte[] BuildFileDescBody(byte[] fileId, string fileName)
    {
        var nameBytes = Encoding.UTF8.GetBytes(fileName);
        var paddedLen = ((nameBytes.Length + 3) / 4) * 4;
        var body = new byte[56 + paddedLen];
        fileId.CopyTo(body, 0);
        nameBytes.CopyTo(body, 56);
        return body;
    }

    private static byte[] BuildUniFileNBody(byte[] fileId, string fileName)
    {
        var nameBytes = Encoding.Unicode.GetBytes(fileName);
        var paddedLen = ((nameBytes.Length + 3) / 4) * 4;
        var body = new byte[16 + paddedLen];
        fileId.CopyTo(body, 0);
        nameBytes.CopyTo(body, 16);
        return body;
    }

    private static void WritePacket(Stream stream, string packetType, byte[] body)
    {
        var headerSize = Marshal.SizeOf<Par2PacketHeader>();
        var header = new Par2PacketHeader
        {
            Magic = "PAR2\0PKT"u8.ToArray(),
            PacketLength = (ulong)(headerSize + body.Length),
            PacketHash = new byte[16],
            RecoverySetID = new byte[16],
            PacketType = Encoding.ASCII.GetBytes(packetType.PadRight(16, '\0')[..16]),
        };

        var headerBytes = new byte[headerSize];
        var handle = GCHandle.Alloc(headerBytes, GCHandleType.Pinned);
        try
        {
            Marshal.StructureToPtr(header, handle.AddrOfPinnedObject(), false);
        }
        finally
        {
            handle.Free();
        }

        stream.Write(headerBytes);
        stream.Write(body);
    }
}
