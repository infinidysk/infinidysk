using System.Buffers;
using System.IO.Hashing;
using System.Security.Cryptography;
using NzbWebDAV.Par2Recovery.Packets;

namespace NzbWebDAV.Par2Recovery.ReedSolomon;

#pragma warning disable CA5351 // PAR 2.0 IFSC/file hashes use MD5 per spec

/// <summary>
/// Bounded-memory PAR2 Reed-Solomon reconstruction for a single recovery set.
/// </summary>
public sealed class Par2Reconstructor
{
    private readonly Gf16Field _field = new();

    public sealed record RecoverySlice(uint Exponent, byte[] Data);

    public sealed record ReconstructionResult(
        bool Success,
        Dictionary<int, byte[]> ReconstructedSlices,
        string? FailureReason);

    /// <summary>
    /// Reconstruct missing slice indices using collected recovery slices and present data fetches.
    /// Whole-file MD5 is computed during the mandatory reduction read — no separate download pass.
    /// </summary>
    public async Task<ReconstructionResult> ReconstructAsync(
        MainPacket main,
        IReadOnlyDictionary<string, FileDesc> fileDescsById,
        IReadOnlyDictionary<string, IfscPacket> ifscsByFileId,
        IReadOnlyList<int> missingSliceIndices,
        IReadOnlyList<RecoverySlice> recoverySlices,
        Func<int, int, CancellationToken, Task<byte[]?>> fetchSliceBytesAsync,
        CancellationToken ct)
    {
        var k = missingSliceIndices.Count;
        if (k == 0)
            return new ReconstructionResult(true, new Dictionary<int, byte[]>(), null);

        if (recoverySlices.Count < k)
            return Fail($"Need {k} recovery slices but only {recoverySlices.Count} available.");

        var sliceSize = (int)main.SliceSize;
        var selected = recoverySlices.Take(k).ToList();
        var missingSet = new HashSet<int>(missingSliceIndices);

        var matrix = new ushort[k][];
        for (var row = 0; row < k; row++)
        {
            matrix[row] = new ushort[k];
            var exp = selected[row].Exponent;
            for (var col = 0; col < k; col++)
                matrix[row][col] = _field.RecoveryCoefficient(exp, missingSliceIndices[col]);
        }

        if (!TryInvert(matrix, k))
            return Fail("Recovery slice coefficient matrix is not invertible.");

        var accumulators = selected.Select(s =>
        {
            var words = new ushort[sliceSize / 2];
            for (var w = 0; w < words.Length; w++)
                words[w] = BitConverter.ToUInt16(s.Data, w * 2);
            return words;
        }).ToArray();

        var sliceBuffer = ArrayPool<byte>.Shared.Rent(sliceSize);
        var pendingFileHashes = new List<(FileDesc desc, IncrementalHash hasher, long bytesHashed, bool hashContiguous)>();
        try
        {
            var globalSlice = 0;
            for (var fileIndex = 0; fileIndex < main.FileIds.Count; fileIndex++)
            {
                ct.ThrowIfCancellationRequested();
                var fileId = main.FileIds[fileIndex];
                var key = Convert.ToHexString(fileId);
                if (!fileDescsById.TryGetValue(key, out var desc))
                    return Fail($"FileDesc missing for FileID {key}.");
                if (!ifscsByFileId.TryGetValue(key, out var ifsc))
                    return Fail($"IFSC missing for FileID {key}.");

#pragma warning disable CA2000 // IncrementalHash instances are disposed in the outer finally block
                var fileMd5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
#pragma warning restore CA2000
                long bytesHashed = 0;
                bool hashContiguous = true;

                for (var local = 0; local < ifsc.Slices.Count; local++)
                {
                    ct.ThrowIfCancellationRequested();
                    var sliceIndex = globalSlice + local;
                    var checksum = ifsc.Slices[local];
                    var isMissing = missingSet.Contains(sliceIndex);

                    byte[]? fetched = await fetchSliceBytesAsync(sliceIndex, sliceSize, ct).ConfigureAwait(false);
                    if (fetched == null)
                    {
                        if (!isMissing)
                            return Fail($"Unexpected missing slice {sliceIndex} during reduction.");
                        Array.Clear(sliceBuffer, 0, sliceSize);
                        hashContiguous = false;
                    }
                    else
                    {
                        if (fetched.Length > sliceSize)
                            return Fail($"Slice {sliceIndex} longer than slice size.");
                        Array.Clear(sliceBuffer, 0, sliceSize);
                        fetched.AsSpan(0, fetched.Length).CopyTo(sliceBuffer);
                        if (!VerifySliceChecksum(sliceBuffer, checksum))
                        {
                            if (isMissing)
                            {
                                Array.Clear(sliceBuffer, 0, sliceSize);
                                hashContiguous = false;
                            }
                            else
                                return Fail($"Present slice {sliceIndex} failed IFSC verification.");
                        }
                    }

                    if (!isMissing && hashContiguous)
                    {
                        var validLen = (int)Math.Min(sliceSize, (long)desc.FileLength - bytesHashed);
                        if (validLen > 0)
                            fileMd5.AppendData(sliceBuffer.AsSpan(0, validLen));
                        bytesHashed += validLen;
                    }
                    else if (!isMissing)
                    {
                        var advance = (int)Math.Min(sliceSize, (long)desc.FileLength - bytesHashed);
                        bytesHashed += advance;
                    }

                    if (!isMissing)
                    {
                        for (var r = 0; r < k; r++)
                        {
                            var coeff = _field.RecoveryCoefficient(selected[r].Exponent, sliceIndex);
                            if (coeff == 0) continue;
                            var acc = accumulators[r];
                            for (var w = 0; w < sliceSize / 2; w++)
                            {
                                var word = BitConverter.ToUInt16(sliceBuffer, w * 2);
                                acc[w] = _field.Add(acc[w], _field.Mul(coeff, word));
                            }
                        }
                    }
                }

                pendingFileHashes.Add((desc, fileMd5, bytesHashed, hashContiguous));
                globalSlice += ifsc.Slices.Count;
            }

            var reconstructed = new Dictionary<int, byte[]>();
            for (var mi = 0; mi < k; mi++)
            {
                var missingIndex = missingSliceIndices[mi];
                var outSlice = new byte[sliceSize];
                for (var w = 0; w < sliceSize / 2; w++)
                {
                    ushort value = 0;
                    for (var j = 0; j < k; j++)
                        value = _field.Add(value, _field.Mul(matrix[mi][j], accumulators[j][w]));
                    BitConverter.TryWriteBytes(outSlice.AsSpan(w * 2, 2), value);
                }

                var (fileIdx, localIdx) = MapGlobalSlice(missingIndex, main, ifscsByFileId);
                var ifscKey = Convert.ToHexString(main.FileIds[fileIdx]);
                var ifsc = ifscsByFileId[ifscKey];
                if (!VerifySliceChecksum(outSlice, ifsc.Slices[localIdx]))
                    return Fail($"Reconstructed slice {missingIndex} failed IFSC verification.");

                reconstructed[missingIndex] = outSlice;

                var pending = pendingFileHashes[fileIdx];
                if (pending.hashContiguous)
                {
                    var remaining = (long)pending.desc.FileLength - pending.bytesHashed;
                    if (remaining > 0)
                    {
                        var validLen = (int)Math.Min(sliceSize, remaining);
                        pending.hasher.AppendData(outSlice.AsSpan(0, validLen));
                        pendingFileHashes[fileIdx] = (pending.desc, pending.hasher, pending.bytesHashed + validLen, true);
                    }
                }
            }

            foreach (var (desc, hasher, bytesHashed, hashContiguous) in pendingFileHashes)
            {
                if (!hashContiguous)
                    continue;
                if (bytesHashed != (long)desc.FileLength)
                    return Fail($"Incomplete whole-file MD5 coverage for {desc.FileName}.");
                var computed = hasher.GetHashAndReset();
                if (!computed.AsSpan().SequenceEqual(desc.FileHash))
                    return Fail($"Whole-file MD5 mismatch for {desc.FileName}.");
            }

            return new ReconstructionResult(true, reconstructed, null);
        }
        finally
        {
            foreach (var (_, hasher, _, _) in pendingFileHashes)
                hasher.Dispose();
            ArrayPool<byte>.Shared.Return(sliceBuffer);
        }
    }

    private static (int fileIndex, int localSlice) MapGlobalSlice(
        int globalSlice,
        MainPacket main,
        IReadOnlyDictionary<string, IfscPacket> ifscsByFileId)
    {
        var offset = 0;
        for (var fi = 0; fi < main.FileIds.Count; fi++)
        {
            var key = Convert.ToHexString(main.FileIds[fi]);
            var count = ifscsByFileId[key].Slices.Count;
            if (globalSlice < offset + count)
                return (fi, globalSlice - offset);
            offset += count;
        }

        throw new ArgumentOutOfRangeException(nameof(globalSlice));
    }

    internal static bool VerifySliceChecksum(byte[] slice, IfscPacket.SliceChecksum checksum)
    {
        var md5 = MD5.HashData(slice);
        if (!md5.AsSpan().SequenceEqual(checksum.Md5)) return false;
        var crc = BitConverter.ToUInt32(Crc32.Hash(slice));
        return crc == checksum.Crc32;
    }

    private bool TryInvert(ushort[][] matrix, int n)
    {
        var inv = new ushort[n][];
        for (var i = 0; i < n; i++)
        {
            inv[i] = new ushort[n];
            inv[i][i] = 1;
        }

        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            while (pivot < n && matrix[pivot][col] == 0)
                pivot++;
            if (pivot == n) return false;

            if (pivot != col)
            {
                (matrix[col], matrix[pivot]) = (matrix[pivot], matrix[col]);
                (inv[col], inv[pivot]) = (inv[pivot], inv[col]);
            }

            var pivotVal = matrix[col][col];
            for (var c = 0; c < n; c++)
            {
                matrix[col][c] = _field.Div(matrix[col][c], pivotVal);
                inv[col][c] = _field.Div(inv[col][c], pivotVal);
            }

            for (var row = 0; row < n; row++)
            {
                if (row == col) continue;
                var factor = matrix[row][col];
                if (factor == 0) continue;
                for (var c = 0; c < n; c++)
                {
                    matrix[row][c] = _field.Add(matrix[row][c], _field.Mul(factor, matrix[col][c]));
                    inv[row][c] = _field.Add(inv[row][c], _field.Mul(factor, inv[col][c]));
                }
            }
        }

        for (var i = 0; i < n; i++)
            matrix[i] = inv[i];

        return true;
    }

    private static ReconstructionResult Fail(string reason)
        => new(false, new Dictionary<int, byte[]>(), reason);
}

#pragma warning restore CA5351
