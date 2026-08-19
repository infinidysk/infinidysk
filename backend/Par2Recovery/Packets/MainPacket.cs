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
        if (body.Length < 12)
            throw new InvalidDataException("Main packet body too short.");

        SliceSize = BitConverter.ToUInt64(body, 0);
        RecoverySetFileCount = BitConverter.ToUInt32(body, 8);

        if (SliceSize < MinSliceSize || SliceSize > MaxSliceSize)
            throw new InvalidDataException($"Main packet slice size {SliceSize} out of range.");

        if (RecoverySetFileCount > MaxFileCount)
            throw new InvalidDataException($"Main packet file count {RecoverySetFileCount} out of range.");

        var expectedBody = 12 + RecoverySetFileCount * 16;
        if (body.Length < expectedBody)
            throw new InvalidDataException("Main packet FileID list truncated.");

        var ids = new List<byte[]>((int)RecoverySetFileCount);
        for (var i = 0; i < RecoverySetFileCount; i++)
        {
            var id = new byte[16];
            Buffer.BlockCopy(body, 12 + i * 16, id, 0, 16);
            ids.Add(id);
        }

        FileIds = ids;
    }
}
