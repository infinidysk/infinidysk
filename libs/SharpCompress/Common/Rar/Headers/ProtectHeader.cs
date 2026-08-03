using SharpCompress.Common.Rar;

namespace SharpCompress.Common.Rar.Headers;

internal sealed class ProtectHeader : RarHeader
{
    public static ProtectHeader Create(RarHeader header, RarBlockBuffer reader)
    {
        var c = CreateChild<ProtectHeader>(header, reader, HeaderType.Protect);
        if (c.IsRar5)
        {
            throw new InvalidFormatException("unexpected rar5 record");
        }
        return c;
    }

    protected override void ReadFinish(RarBlockBuffer reader)
    {
        Version = reader.ReadByte();
        RecSectors = reader.ReadUInt16();
        TotalBlocks = reader.ReadUInt32();
        Mark = reader.ReadBytes(8);
    }

    internal uint DataSize => checked((uint)AdditionalDataSize);
    internal byte Version { get; private set; }
    internal ushort RecSectors { get; private set; }
    internal uint TotalBlocks { get; private set; }
    internal byte[]? Mark { get; private set; }
}
