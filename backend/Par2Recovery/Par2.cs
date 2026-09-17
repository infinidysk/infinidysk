using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Par2Recovery.Packets;
using Serilog;

namespace NzbWebDAV.Par2Recovery
{
    public static class Par2
    {
        internal static readonly Regex ParVolume = new(
            @"(.+)\.vol[0-9]{1,10}\+[0-9]{1,10}\.par2$",
            RegexOptions.IgnoreCase
        );

        private const string Par2PacketHeaderMagic = "PAR2\0PKT";

        /// <summary>
        /// Reads checksum-verified metadata without reading recovery bodies. Malformed,
        /// truncated, or conflicting metadata throws before any descriptions are yielded.
        /// A complete coherent metadata prefix may supply proofs when stopping at recovery data.
        /// </summary>
        public static async IAsyncEnumerable<FileDesc> ReadVerifiedFileDescriptions
        (
            Stream stream,
            bool stopAtRecoverySlice = false,
            [EnumeratorCancellation] CancellationToken ct = default
        )
        {
            if (!stream.CanSeek)
                throw new NotSupportedException("Verified PAR2 metadata requires a seekable stream to skip recovery bodies.");

            var budget = new Par2MemoryBudget(256L * 1024 * 1024);
            var options = new Par2RepairReader.ReadOptions(budget, RetainRecoveryPayload: false);
            var packets = new Dictionary<string, Par2Packet>(StringComparer.Ordinal);
            var header = new byte[64];
            try
            {
                while (stream.Position < stream.Length)
                {
                    ct.ThrowIfCancellationRequested();
                    var position = stream.Position;
                    await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
                    if (!header.AsSpan(0, 8).SequenceEqual("PAR2\0PKT"u8))
                        throw new InvalidDataException("Invalid PAR2 magic constant.");

                    var length = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8));
                    if (length < 64 || length > int.MaxValue || length % 4 != 0)
                        throw new InvalidDataException($"Invalid PAR2 packet length {length}.");
                    var bodyLength = (int)length - 64;
                    if (bodyLength > stream.Length - stream.Position)
                        throw new EndOfStreamException("Truncated PAR2 packet body.");

                    var packetType = Encoding.ASCII.GetString(header, 48, 16).TrimEnd('\0');
                    if (packetType == RecvSlic.PacketType)
                    {
                        if (bodyLength < 4 || bodyLength - 4 > (long)MainPacket.MaxSliceSize)
                            throw new InvalidDataException("Invalid PAR2 recovery packet length.");
                        stream.Seek(bodyLength, SeekOrigin.Current);
                        if (stopAtRecoverySlice)
                            break;
                        continue;
                    }

                    if (packetType is not (MainPacket.PacketType or FileDesc.PacketType
                        or IfscPacket.PacketType or UniFileN.PacketType))
                    {
                        stream.Seek(bodyLength, SeekOrigin.Current);
                        continue;
                    }

                    stream.Seek(position, SeekOrigin.Begin);
                    var packet = await Par2RepairReader.ReadVerifiedPacketAsync(stream, options, ct)
                        .ConfigureAwait(false);
                    var fileId = packet switch
                    {
                        FileDesc description => Convert.ToHexString(description.FileID),
                        IfscPacket checksums => Convert.ToHexString(checksums.FileId),
                        UniFileN unicodeName => Convert.ToHexString(unicodeName.FileID),
                        _ => string.Empty,
                    };
                    var key = Convert.ToHexString(packet.Header.RecoverySetID) + packetType + fileId;
                    if (packets.TryGetValue(key, out var existing))
                    {
                        packet.ReleaseMemory();
                        if (!packet.Header.PacketHash.AsSpan().SequenceEqual(existing.Header.PacketHash))
                            throw new InvalidDataException("Conflicting duplicate PAR2 metadata packet.");
                    }
                    else
                    {
                        packets.Add(key, packet);
                    }
                }

                var mainByFile = new Dictionary<string, MainPacket>(StringComparer.Ordinal);
                foreach (var main in packets.Values.OfType<MainPacket>())
                {
                    var setId = Convert.ToHexString(main.Header.RecoverySetID);
                    foreach (var fileId in main.FileIds)
                        mainByFile.Add(setId + Convert.ToHexString(fileId), main);
                }

                foreach (var description in packets.Values.OfType<FileDesc>())
                {
                    ct.ThrowIfCancellationRequested();
                    var setId = Convert.ToHexString(description.Header.RecoverySetID);
                    var fileId = Convert.ToHexString(description.FileID);
                    if (packets.TryGetValue(setId + UniFileN.PacketType + fileId, out var namePacket)
                        && namePacket is UniFileN { FileName.Length: > 0 } unicodeName)
                        description.FileName = unicodeName.FileName;

                    if (!mainByFile.TryGetValue(setId + fileId, out var main)
                        || !packets.TryGetValue(setId + IfscPacket.PacketType + fileId, out var slicePacket)
                        || slicePacket is not IfscPacket checksums
                        || description.FileLength == 0 || description.FileLength > long.MaxValue
                        || main.SliceSize > 32 * 1024 * 1024
                        || (description.FileLength - 1) / main.SliceSize + 1 != (ulong)checksums.Slices.Count)
                        continue;

                    budget.Charge(512L + checksums.Slices.Count * 20L);
                    var proof = new Par2FileProof
                    {
                        FileLength = (long)description.FileLength,
                        SliceSize = (int)main.SliceSize,
                        SliceMd5 = new byte[checksums.Slices.Count * 16],
                        SliceCrc32 = new uint[checksums.Slices.Count],
                        FileId = description.FileID.ToArray(),
                        FileHash = description.FileHash.ToArray(),
                        File16kHash = description.File16kHash.ToArray(),
                    };
                    for (var sliceIndex = 0; sliceIndex < checksums.Slices.Count; sliceIndex++)
                    {
                        checksums.Slices[sliceIndex].Md5.CopyTo(proof.SliceMd5, sliceIndex * 16);
                        proof.SliceCrc32[sliceIndex] = checksums.Slices[sliceIndex].Crc32;
                    }
                    if (proof.IsValidFor((long)description.FileLength))
                        description.VerificationProof = proof;
                }

                foreach (var description in packets.Values.OfType<FileDesc>())
                {
                    ct.ThrowIfCancellationRequested();
                    yield return description;
                }
            }
            finally
            {
                foreach (var packet in packets.Values)
                    packet.ReleaseMemory();
            }
        }

        public static async IAsyncEnumerable<FileDesc> ReadFileDescriptions
        (
            Stream stream,
            bool stopAtRecoverySlice = false,
            [EnumeratorCancellation] CancellationToken ct = default
        )
        {
            // Buffer descriptors and optional UniFileN names so Unicode can win
            // regardless of packet order (UniFileN may appear before or after FileDesc).
            var fileDescs = new List<FileDesc>();
            var unicodeNamesByFileId = new Dictionary<string, string>(StringComparer.Ordinal);

            while (stream.Position < stream.Length && !ct.IsCancellationRequested)
            {
                Par2Packet packet;
                try
                {
                    packet = await ReadPacketAsync(stream).ConfigureAwait(false);
                }
                catch (Exception e) when (!e.IsCancellationException(ct))
                {
                    Log.Warning(e, "Failed to read PAR2 packet");
                    break;
                }

                switch (packet)
                {
                    case FileDesc fileDesc:
                        fileDescs.Add(fileDesc);
                        break;
                    case UniFileN uniFileN:
                        unicodeNamesByFileId[Convert.ToHexString(uniFileN.FileID)] = uniFileN.FileName;
                        break;
                }

                // Recovery volumes repeat the index packets before the recovery
                // data, so by the first RecvSlic we already hold every FileDesc
                // the file contains. Used to recognize obfuscated recovery
                // volumes whose NZB subjects do not match the vol regex.
                if (stopAtRecoverySlice && packet is RecvSlic)
                    break; // volumes duplicate index descriptors before recovery data
            }

            foreach (var fileDesc in fileDescs)
            {
                if (unicodeNamesByFileId.TryGetValue(Convert.ToHexString(fileDesc.FileID), out var unicodeName)
                    && !string.IsNullOrEmpty(unicodeName))
                {
                    fileDesc.FileName = unicodeName;
                }

                yield return fileDesc;
            }
        }

        private static async Task<Par2Packet> ReadPacketAsync(Stream stream)
        {
            // Read a Packet Header.
            var header = await ReadStructAsync<Par2PacketHeader>(stream).ConfigureAwait(false);

            // Test if the magic constant matches.
            var magic = Encoding.ASCII.GetString(header.Magic);
            if (!Par2PacketHeaderMagic.Equals(magic, StringComparison.Ordinal))
                throw new InvalidDataException("Invalid Magic Constant");

            // Determine which type of packet we have.
            var packetType = Encoding.ASCII.GetString(header.PacketType);
            Par2Packet result;
            switch (packetType)
            {
                case FileDesc.PacketType:
                    result = new FileDesc(header);
                    break;
                case UniFileN.PacketType:
                    result = new UniFileN(header);
                    break;
                case RecvSlic.PacketType:
                    result = new RecvSlic(header);
                    break;
                default:
                    result = new Par2Packet(header);
                    break;
            }

            // Let the packet type parse more of the stream as needed.
            await result.ReadAsync(stream).ConfigureAwait(false);

            return result;
        }

        /// <summary>
        /// Read a struct as binary from a stream.
        /// </summary>
        /// <typeparam name="T">The struct to read.</typeparam>
        /// <param name="stream">The stream to read from.</param>
        /// <returns>The struct with values read from the stream.</returns>
        private static async Task<T> ReadStructAsync<T>(Stream stream) where T : struct
        {
            var size = Marshal.SizeOf<T>();
            var buffer = new byte[size];
            await stream.ReadExactlyAsync(buffer.AsMemory(0, size)).ConfigureAwait(false);
            var pinnedBuffer = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                var structure = Marshal.PtrToStructure<T>(pinnedBuffer.AddrOfPinnedObject());
                return structure;
            }
            finally
            {
                pinnedBuffer.Free();
            }
        }

        private static T ReadStruct<T>(byte[] bytes) where T : struct
        {
            var size = Marshal.SizeOf<T>();
            if (bytes.Length < size)
            {
                throw new ArgumentException("Byte array is too short to represent the struct.", nameof(bytes));
            }

            var pinnedBuffer = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                var structure = Marshal.PtrToStructure<T>(pinnedBuffer.AddrOfPinnedObject());
                return structure;
            }
            finally
            {
                pinnedBuffer.Free();
            }
        }

        public static bool HasPar2MagicBytes(byte[] bytes)
        {
            try
            {
                var header = ReadStruct<Par2PacketHeader>(bytes);
                var magic = Encoding.ASCII.GetString(header.Magic);
                return Par2PacketHeaderMagic.Equals(magic, StringComparison.Ordinal);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return false;
            }
        }
    }
}
