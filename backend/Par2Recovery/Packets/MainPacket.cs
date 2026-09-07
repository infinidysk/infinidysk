using System.Buffers.Binary;

namespace NzbWebDAV.Par2Recovery.Packets;

/// <summary>
/// PAR 2.0 Main packet ("PAR 2.0\0Main").
/// </summary>
public sealed class MainPacket : Par2Packet
{
    public const string PacketType = "PAR 2.0\0Main";

    public const ulong MinSliceSize = 4 * 1024;
    public const ulong MaxSliceSize = 64 * 1024 * 1024;
    public const uint MaxFileCount = 100_000;

    public ulong SliceSize { get; private set; }
    public uint RecoverySetFileCount { get; private set; }
    public IReadOnlyList<byte[]> FileIds { get; private set; } = [];

    public MainPacket(Par2PacketHeader header) : base(header)
    {
    }

    protected override void ParseBody(byte[] body)
    {
        if (body.Length < 12 || (body.Length - 12) % 16 != 0)
            throw new InvalidDataException("Main packet body too short.");

        SliceSize = BinaryPrimitives.ReadUInt64LittleEndian(body);
        RecoverySetFileCount = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8));

        if (SliceSize < MinSliceSize || SliceSize > MaxSliceSize || SliceSize % 4 != 0)
            throw new InvalidDataException($"Main packet slice size {SliceSize} out of range.");

        if (RecoverySetFileCount > MaxFileCount)
            throw new InvalidDataException($"Main packet file count {RecoverySetFileCount} out of range.");

        var expectedBody = 12 + RecoverySetFileCount * 16;
        if (body.Length < expectedBody)
            throw new InvalidDataException("Main packet FileID list truncated.");

        var ids = new List<byte[]>((int)RecoverySetFileCount);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < RecoverySetFileCount; i++)
        {
            var id = new byte[16];
            Buffer.BlockCopy(body, 12 + i * 16, id, 0, 16);
            if (!seen.Add(Convert.ToHexString(id)))
                throw new InvalidDataException("Main packet contains duplicate recoverable FileIDs.");
            ids.Add(id);
        }

        FileIds = ids;
    }
}
