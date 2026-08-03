using SharpCompress.Common.Rar;

namespace SharpCompress.Common.Rar.Headers;

internal sealed class ArchiveCryptHeader : RarHeader
{
    public static ArchiveCryptHeader Create(RarHeader header, RarBlockBuffer reader) =>
        CreateChild<ArchiveCryptHeader>(header, reader, HeaderType.Crypt);

    public Rar5CryptoInfo CryptInfo = default!;

    protected override void ReadFinish(RarBlockBuffer reader) =>
        CryptInfo = Rar5CryptoInfo.Create(reader, false);
}
