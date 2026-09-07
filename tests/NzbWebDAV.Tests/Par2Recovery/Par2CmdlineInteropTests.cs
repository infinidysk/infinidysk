using System.Buffers.Binary;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using NzbWebDAV.Par2Recovery;
using NzbWebDAV.Par2Recovery.Packets;
using NzbWebDAV.Par2Recovery.ReedSolomon;

namespace NzbWebDAV.Tests.Par2Recovery;

#pragma warning disable CA5351 // PAR2 specifies MD5 for packet and file integrity.

public sealed class Par2CmdlineInteropTests
{
    internal static string CorpusDirectory => Path.Join(AppContext.BaseDirectory, "TestFixtures", "Par2Corpus");

    [Fact]
    public void CommittedCorpus_MatchesManifest()
    {
        foreach (var line in File.ReadLines(Path.Join(CorpusDirectory, "SHA256SUMS")))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var bytes = File.ReadAllBytes(Path.Join(CorpusDirectory, fields[1]));
            Assert.Equal(fields[0].ToUpperInvariant(), Convert.ToHexString(SHA256.HashData(bytes)));
        }
    }

    [Theory]
    [InlineData("single", 4096UL, 4)]
    [InlineData("set", 4096UL, 8)]
    [InlineData("uneven", 6144UL, 2)]
    public async Task PacketsAndSourceChecksums_MatchReferenceCorpus(string prefix, ulong sliceSize, int recoveryCount)
    {
        var corpus = await ReadSetAsync(prefix);
        Assert.Equal(sliceSize, corpus.Main.SliceSize);
        Assert.Equal(recoveryCount, corpus.Recovery.Count);
        foreach (var key in corpus.Main.FileIds.Select(Convert.ToHexString))
        {
            var descriptor = corpus.Descriptors[key];
            var bytes = File.ReadAllBytes(Path.Join(CorpusDirectory, descriptor.FileName));
            Assert.Equal((ulong)bytes.Length, descriptor.FileLength);
            Assert.Equal(MD5.HashData(bytes), descriptor.FileHash);
            Assert.Equal(MD5.HashData(bytes.AsSpan(0, Math.Min(bytes.Length, 16384))), descriptor.File16kHash);

            var name = Encoding.ASCII.GetBytes(descriptor.FileName);
            var identity = new byte[24 + name.Length];
            descriptor.File16kHash.CopyTo(identity, 0);
            BinaryPrimitives.WriteUInt64LittleEndian(identity.AsSpan(16), descriptor.FileLength);
            name.CopyTo(identity, 24);
            Assert.Equal(MD5.HashData(identity), descriptor.FileID);

            var checksums = corpus.Checksums[key].Slices;
            Assert.Equal((bytes.Length + (int)sliceSize - 1) / (int)sliceSize, checksums.Count);
            var distinctHashes = new HashSet<string>();
            for (var index = 0; index < checksums.Count; index++)
            {
                var slice = GetSlice(bytes, index, (int)sliceSize);
                Assert.Equal(MD5.HashData(slice), checksums[index].Md5);
                Assert.Equal(Crc32.HashToUInt32(slice), checksums[index].Crc32);
                Assert.True(distinctHashes.Add(Convert.ToHexString(checksums[index].Md5)));
            }
        }
    }

    [Theory]
    [InlineData("single", false)]
    [InlineData("set", true)]
    [InlineData("uneven", false)]
    public async Task Reconstruct_RealNonzeroParity_IsByteExact(string prefix, bool crossFile)
    {
        var corpus = await ReadSetAsync(prefix);
        var slices = new List<byte[]>();
        var missing = new List<int>();
        foreach (var fileId in corpus.Main.FileIds)
        {
            var descriptor = corpus.Descriptors[Convert.ToHexString(fileId)];
            var bytes = File.ReadAllBytes(Path.Join(CorpusDirectory, descriptor.FileName));
            if (descriptor.FileName == "alpha.bin" || crossFile && descriptor.FileName == "gamma.bin")
                missing.Add(slices.Count);
            for (var index = 0; index < corpus.Checksums[Convert.ToHexString(fileId)].Slices.Count; index++)
                slices.Add(GetSlice(bytes, index, (int)corpus.Main.SliceSize));
        }

        var recovery = corpus.Recovery.OrderBy(slice => slice.Exponent)
            .Skip(crossFile ? 0 : 1).Take(missing.Count).ToArray();
        var result = await new Par2Reconstructor().ReconstructAsync(
            corpus.Main, corpus.Descriptors, corpus.Checksums, missing, recovery,
            (index, _, _) => Task.FromResult<byte[]?>(missing.Contains(index) ? null : slices[index]),
            CancellationToken.None);

        Assert.True(result.Success, result.FailureReason);
        foreach (var index in missing)
            Assert.Equal(slices[index], result.ReconstructedSlices[index]);
    }

    internal static async Task<CorpusSet> ReadSetAsync(string prefix)
    {
        MainPacket? main = null;
        var descriptors = new Dictionary<string, FileDesc>(StringComparer.Ordinal);
        var checksums = new Dictionary<string, IfscPacket>(StringComparer.Ordinal);
        var recovery = new Dictionary<uint, Par2Reconstructor.RecoverySlice>();
        string? expectedSetId = null;
        foreach (var file in Directory.GetFiles(CorpusDirectory, prefix + "*.par2").Order(StringComparer.Ordinal))
        {
            var bytes = await File.ReadAllBytesAsync(file);
            foreach (var retainPayload in new[] { false, true })
            {
                await using var stream = new MemoryStream(bytes, writable: false);
                while (stream.Position < stream.Length)
                {
                    var offset = checked((int)stream.Position);
                    var length = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset + 8)));
                    var packet = await Par2RepairReader.ReadVerifiedPacketAsync(stream, retainPayload, CancellationToken.None);
                    Assert.Equal(MD5.HashData(bytes.AsSpan(offset + 32, length - 32)), packet.Header.PacketHash);
                    var setId = Convert.ToHexString(packet.Header.RecoverySetID);
                    expectedSetId ??= setId;
                    Assert.Equal(expectedSetId, setId);
                    switch (packet)
                    {
                        case MainPacket mainPacket:
                            Assert.Equal(MD5.HashData(bytes.AsSpan(offset + 64, length - 64)), packet.Header.RecoverySetID);
                            if (main is not null)
                                Assert.Equal(main.Header.PacketHash, packet.Header.PacketHash);
                            main = mainPacket;
                            break;
                        case FileDesc descriptor:
                            AddConsistent(descriptors, Convert.ToHexString(descriptor.FileID), descriptor);
                            break;
                        case IfscPacket checksum:
                            AddConsistent(checksums, Convert.ToHexString(checksum.FileId), checksum);
                            break;
                        case RecvSlic slice when retainPayload:
                            Assert.True(recovery.TryAdd(slice.Exponent, new(slice.Exponent, slice.Payload)));
                            break;
                        case RecvSlic slice:
                            Assert.Empty(slice.Payload);
                            break;
                    }
                }
            }
        }

        Assert.NotNull(main);
        return new CorpusSet(main, descriptors, checksums, recovery.Values.ToArray());
    }

    private static void AddConsistent<TPacket>(Dictionary<string, TPacket> packets, string key, TPacket packet)
        where TPacket : Par2Packet
    {
        if (!packets.TryAdd(key, packet))
            Assert.Equal(packets[key].Header.PacketHash, packet.Header.PacketHash);
    }

    private static byte[] GetSlice(byte[] bytes, int index, int sliceSize)
    {
        var slice = new byte[sliceSize];
        var offset = checked(index * sliceSize);
        bytes.AsSpan(offset, Math.Min(sliceSize, bytes.Length - offset)).CopyTo(slice);
        return slice;
    }

    internal sealed record CorpusSet(MainPacket Main, Dictionary<string, FileDesc> Descriptors,
        Dictionary<string, IfscPacket> Checksums, IReadOnlyList<Par2Reconstructor.RecoverySlice> Recovery);
}

#pragma warning restore CA5351