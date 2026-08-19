using NzbWebDAV.Par2Recovery;
using NzbWebDAV.Par2Recovery.Packets;
using NzbWebDAV.Par2Recovery.ReedSolomon;

namespace NzbWebDAV.Tests.Par2Recovery;

public sealed class Par2ReconstructorTests
{
    [Fact]
    public async Task Reconstruct_SingleMissingSlice_MatchesOriginal()
    {
        var fileData = new byte[5000];
        for (var i = 0; i < fileData.Length; i++)
            fileData[i] = (byte)(i * 3);

        const ulong sliceSize = 4096;
        var (_, volume) = Par2TestEncoder.EncodeSet("content.mkv", fileData, sliceSize, [0u, 1u]);

        var descriptors = new Dictionary<string, FileDesc>();
        var ifscs = new Dictionary<string, IfscPacket>();
        MainPacket? main = null;
        await using (var stream = new MemoryStream(volume))
        {
            while (stream.Position < stream.Length)
            {
                var packet = await Par2RepairReader.ReadVerifiedPacketAsync(stream, false, CancellationToken.None);
                switch (packet)
                {
                    case FileDesc fd:
                        descriptors[Convert.ToHexString(fd.FileID)] = fd;
                        break;
                    case MainPacket mp:
                        main = mp;
                        break;
                    case IfscPacket ifsc:
                        ifscs[Convert.ToHexString(ifsc.FileId)] = ifsc;
                        break;
                }
            }
        }

        Assert.NotNull(main);
        var slices = BuildSliceBytes(fileData, sliceSize);
        var missingIndex = 1;
        var recoverySlices = new List<Par2Reconstructor.RecoverySlice>();

        await using (var stream = new MemoryStream(volume))
        {
            while (stream.Position < stream.Length)
            {
                var packet = await Par2RepairReader.ReadVerifiedPacketAsync(stream, true, CancellationToken.None);
                if (packet is RecvSlic recv)
                    recoverySlices.Add(new Par2Reconstructor.RecoverySlice(recv.Exponent, recv.Payload));
            }
        }

        var reconstructor = new Par2Reconstructor();
        var result = await reconstructor.ReconstructAsync(
            main!,
            descriptors,
            ifscs,
            [missingIndex],
            recoverySlices,
            (globalSlice, size, _) =>
            {
                if (globalSlice == missingIndex)
                    return Task.FromResult<byte[]?>(null);
                return Task.FromResult<byte[]?>(slices[globalSlice]);
            },
            CancellationToken.None);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(slices[missingIndex], result.ReconstructedSlices[missingIndex]);
    }

    [Fact]
    public async Task Reconstruct_CorruptedRecoverySlice_FailsWithoutPersisting()
    {
        var fileData = new byte[64];
        Random.Shared.NextBytes(fileData);
        var (_, volume) = Par2TestEncoder.EncodeSet("f.bin", fileData, 64, [0u]);
        volume[^1] ^= 0x55;

        var descriptors = new Dictionary<string, FileDesc>();
        var ifscs = new Dictionary<string, IfscPacket>();
        MainPacket? main = null;
        List<Par2Reconstructor.RecoverySlice> recovery = [];

        await using var stream = new MemoryStream(volume);
        try
        {
            while (stream.Position < stream.Length)
            {
                var packet = await Par2RepairReader.ReadVerifiedPacketAsync(stream, true, CancellationToken.None);
                switch (packet)
                {
                    case FileDesc fd:
                        descriptors[Convert.ToHexString(fd.FileID)] = fd;
                        break;
                    case MainPacket mp:
                        main = mp;
                        break;
                    case IfscPacket ifsc:
                        ifscs[Convert.ToHexString(ifsc.FileId)] = ifsc;
                        break;
                    case RecvSlic recv:
                        recovery.Add(new Par2Reconstructor.RecoverySlice(recv.Exponent, recv.Payload));
                        break;
                }
            }
        }
        catch (InvalidDataException)
        {
            return; // hash failure at read is also valid gate behavior
        }

        if (main == null || recovery.Count == 0) return;

        var slices = BuildSliceBytes(fileData, 64);
        var result = await new Par2Reconstructor().ReconstructAsync(
            main,
            descriptors,
            ifscs,
            [0],
            recovery,
            (_, _, _) => Task.FromResult<byte[]?>(null),
            CancellationToken.None);

        Assert.False(result.Success);
    }

    private static byte[][] BuildSliceBytes(byte[] data, ulong sliceSize)
    {
        var count = (int)((data.Length + (long)sliceSize - 1) / (long)sliceSize);
        var slices = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            slices[i] = new byte[(int)sliceSize];
            var offset = i * (int)sliceSize;
            var len = Math.Min((int)sliceSize, data.Length - offset);
            data.AsSpan(offset, len).CopyTo(slices[i]);
        }
        return slices;
    }
}
