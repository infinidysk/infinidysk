using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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

    [Fact]
    public async Task ReadVerifiedFileDescriptions_AttachesUsableProofIncludingFinalPadding()
    {
        var data = Enumerable.Range(0, 4103).Select(value => (byte)value).ToArray();
        var packets = BuildVerifiedPackets(data);
        var descriptions = await ReadVerifiedAsync(packets);
        var proof = Assert.Single(descriptions).VerificationProof;

        Assert.NotNull(proof);
        Assert.True(proof.IsValidFor(data.Length));
        Assert.True(proof.VerifySlice(data.AsSpan(0, 4096), 0));
        var padded = new byte[4096];
        data.AsSpan(4096).CopyTo(padded);
        Assert.True(proof.VerifySlice(padded, 1));
        Assert.False(proof.VerifySlice(data.AsSpan(4096), 1));
        padded[^1] = 1;
        Assert.False(proof.VerifySlice(padded, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ReadVerifiedFileDescriptions_RejectsCorruptRequiredPacket(int packetIndex)
    {
        var packets = BuildVerifiedPackets(new byte[4103]);
        packets[packetIndex][^1] ^= 1;
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadVerifiedAsync(packets));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadVerifiedFileDescriptions_RejectsTruncationEvenAfterValidPrefix(bool completePrefix)
    {
        var packets = BuildVerifiedPackets(new byte[4103]);
        var truncated = packets[^1][..^1];
        if (completePrefix)
            packets.Add(truncated);
        else
            packets[^1] = truncated;
        await Assert.ThrowsAsync<EndOfStreamException>(() => ReadVerifiedAsync(packets));
    }

    [Fact]
    public async Task ReadVerifiedFileDescriptions_RejectsBadChecksumAfterValidPrefix()
    {
        var packets = BuildVerifiedPackets(new byte[4103]);
        var corrupted = packets[^1].ToArray();
        corrupted[^1] ^= 1;
        packets.Add(corrupted);
        var yielded = new List<FileDesc>();
        await using var stream = new MemoryStream(packets.SelectMany(packet => packet).ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var description in Par2.ReadVerifiedFileDescriptions(stream))
                yielded.Add(description);
        });
        Assert.Empty(yielded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ReadVerifiedFileDescriptions_RejectsConflictingDuplicates(int packetIndex)
    {
        var packets = BuildVerifiedPackets(new byte[4103]);
        var conflicting = packets[packetIndex].ToArray();
        var offset = packetIndex switch { 0 => 80, 1 => 65, _ => 80 };
        conflicting[offset] ^= 1;
        RehashPacket(conflicting);
        packets.Add(conflicting);
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadVerifiedAsync(packets));
    }

    [Fact]
    public async Task ReadVerifiedFileDescriptions_AcceptsIdenticalDuplicatesAndAnyOrder()
    {
        var packets = BuildVerifiedPackets(new byte[4103]);
        packets.AddRange(packets.Select(packet => packet.ToArray()).ToList());
        packets.Reverse();
        Assert.NotNull(Assert.Single(await ReadVerifiedAsync(packets)).VerificationProof);
    }

    [Theory]
    [InlineData(0, 32)]
    [InlineData(0, 64)]
    [InlineData(1, 32)]
    [InlineData(2, 32)]
    [InlineData(1, 76)]
    [InlineData(2, 64)]
    public async Task ReadVerifiedFileDescriptions_DoesNotMixSetsOrUnboundFileIds(int packetIndex, int offset)
    {
        var packets = BuildVerifiedPackets(new byte[4103]);
        packets[packetIndex][offset] ^= 1;
        RehashPacket(packets[packetIndex]);
        Assert.Null(Assert.Single(await ReadVerifiedAsync(packets)).VerificationProof);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ReadVerifiedFileDescriptions_RequiresCompleteMetadata(int missingPacket)
    {
        var packets = BuildVerifiedPackets(new byte[4103]);
        packets.RemoveAt(missingPacket);
        Assert.Null(Assert.Single(await ReadVerifiedAsync(packets)).VerificationProof);
    }

    [Theory]
    [InlineData(-20)]
    [InlineData(20)]
    public async Task ReadVerifiedFileDescriptions_RequiresExactSliceCount(int difference)
    {
        var packets = BuildVerifiedPackets(new byte[4103]);
        var checksums = packets[^1];
        Array.Resize(ref checksums, checksums.Length + difference);
        BinaryPrimitives.WriteUInt64LittleEndian(checksums.AsSpan(8), (ulong)checksums.Length);
        RehashPacket(checksums);
        packets[^1] = checksums;
        Assert.Null(Assert.Single(await ReadVerifiedAsync(packets)).VerificationProof);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadVerifiedFileDescriptions_SeeksRecoveryBodies(bool stopAtRecoverySlice)
    {
        var packets = BuildVerifiedPackets(new byte[4103]);
        await using var stream = new ReadTrackingMemoryStream();
        foreach (var packet in packets)
            stream.Write(packet);
        WritePacket(stream, RecvSlic.PacketType, new byte[5 * 1024 * 1024 + 4]);
        var recoveryEnd = stream.Position;
        var trailing = BuildVerifiedPackets(new byte[4104]);
        foreach (var packet in trailing)
            stream.Write(packet);
        stream.Position = 0;

        var descriptions = new List<FileDesc>();
        await foreach (var description in Par2.ReadVerifiedFileDescriptions(stream, stopAtRecoverySlice))
            descriptions.Add(description);
        Assert.Equal(stopAtRecoverySlice ? 1 : 2, descriptions.Count);
        Assert.All(descriptions, description => Assert.NotNull(description.VerificationProof));
        Assert.True(stream.TotalBytesRead < 64 * 1024);
        Assert.Equal(stopAtRecoverySlice ? recoveryEnd : stream.Length, stream.Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadVerifiedFileDescriptions_StopRequiresCompleteMetadataPrefix(bool stopAtRecoverySlice)
    {
        var packets = BuildVerifiedPackets(new byte[4103]);
        await using var stream = new MemoryStream();
        stream.Write(packets[0]);
        stream.Write(packets[1]);
        WritePacket(stream, RecvSlic.PacketType, new byte[4100]);
        stream.Write(packets[2]);
        stream.Position = 0;
        var descriptions = new List<FileDesc>();
        await foreach (var description in Par2.ReadVerifiedFileDescriptions(stream, stopAtRecoverySlice))
            descriptions.Add(description);
        Assert.Equal(!stopAtRecoverySlice, Assert.Single(descriptions).VerificationProof is not null);
    }

    private static List<byte[]> BuildVerifiedPackets(byte[] data)
    {
        var (index, _) = Par2TestEncoder.EncodeSet("verified.bin", data, 4096, []);
        var packets = new List<byte[]>();
        for (var offset = 0; offset < index.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt64LittleEndian(index.AsSpan(offset + 8));
            var packet = index.AsSpan(offset, length).ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(32), data.Length);
            packet.AsSpan(36, 12).Clear();
            RehashPacket(packet);
            packets.Add(packet);
            offset += length;
        }
        return packets;
    }

#pragma warning disable CA5351 // PAR2 specifies MD5 for packet integrity.
    private static void RehashPacket(byte[] packet) => MD5.HashData(packet.AsSpan(32)).CopyTo(packet, 16);
#pragma warning restore CA5351

    private static async Task<List<FileDesc>> ReadVerifiedAsync(List<byte[]> packets)
    {
        await using var stream = new MemoryStream(packets.SelectMany(packet => packet).ToArray());
        var descriptions = new List<FileDesc>();
        await foreach (var description in Par2.ReadVerifiedFileDescriptions(stream))
            descriptions.Add(description);
        return descriptions;
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
