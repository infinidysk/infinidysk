namespace NzbWebDAV.Par2Recovery.Packets;

/// <summary>
/// PAR 2.0 Input File Slice Check packet ("PAR 2.0\0IFSC").
/// </summary>
public sealed class IfscPacket : Par2Packet
{
    public const string PacketType = "PAR 2.0\0IFSC";

    public byte[] FileId { get; private set; } = [];
    public IReadOnlyList<SliceChecksum> Slices { get; private set; } = [];

    public sealed record SliceChecksum(byte[] Md5, uint Crc32);

    public IfscPacket(Par2PacketHeader header) : base(header)
    {
    }

    protected override void ParseBody(byte[] body)
    {
        if (body.Length < 16)
            throw new InvalidDataException("IFSC packet body too short.");

        FileId = new byte[16];
        Buffer.BlockCopy(body, 0, FileId, 0, 16);

        var remainder = body.Length - 16;
        if (remainder % 20 != 0)
            throw new InvalidDataException("IFSC packet slice list length is not a multiple of 20.");

        var sliceCount = remainder / 20;
        var slices = new List<SliceChecksum>(sliceCount);
        for (var i = 0; i < sliceCount; i++)
        {
            var offset = 16 + i * 20;
            var md5 = new byte[16];
            Buffer.BlockCopy(body, offset, md5, 0, 16);
            var crc = BitConverter.ToUInt32(body, offset + 16);
            slices.Add(new SliceChecksum(md5, crc));
        }

        Slices = slices;
    }
}
