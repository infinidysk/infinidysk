using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Utils;

public class VideoSignatureUtilTests
{
    private static readonly byte[] EbmlMagic = [0x1A, 0x45, 0xDF, 0xA3, 0x00, 0x00, 0x00, 0x00];
    private static readonly byte[] Mp4Magic =
    [
        0x00, 0x00, 0x00, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p',
        (byte)'i', (byte)'s', (byte)'o', (byte)'m',
    ];
    private static readonly byte[] AviMagic =
    [
        (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0x00, 0x00, 0x00, 0x00,
        (byte)'A', (byte)'V', (byte)'I', (byte)' ',
    ];
    private static readonly byte[] WmvMagic =
    [
        0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C,
    ];
    private static readonly byte[] FlvMagic = [0x46, 0x4C, 0x56, 0x01, 0x00, 0x00, 0x00, 0x00];
    private static readonly byte[] MpgMagic = [0x00, 0x00, 0x01, 0xBA, 0x44, 0x00, 0x04, 0x00];
    private static readonly byte[] Rar4Magic = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00, 0x00];
    private static readonly byte[] Par2Magic = [0x50, 0x41, 0x52, 0x32, 0x00, 0x00, 0x00, 0x00];

    [Theory]
    [InlineData(nameof(EbmlMagic), ".mkv")]
    [InlineData(nameof(Mp4Magic), ".mp4")]
    [InlineData(nameof(AviMagic), ".avi")]
    [InlineData(nameof(WmvMagic), ".wmv")]
    [InlineData(nameof(FlvMagic), ".flv")]
    [InlineData(nameof(MpgMagic), ".mpg")]
    public void GuessVideoExtension_DetectsKnownSignatures(string magicName, string expected)
    {
        var magic = magicName switch
        {
            nameof(EbmlMagic) => EbmlMagic,
            nameof(Mp4Magic) => Mp4Magic,
            nameof(AviMagic) => AviMagic,
            nameof(WmvMagic) => WmvMagic,
            nameof(FlvMagic) => FlvMagic,
            nameof(MpgMagic) => MpgMagic,
            _ => throw new ArgumentOutOfRangeException(nameof(magicName)),
        };

        Assert.Equal(expected, VideoSignatureUtil.GuessVideoExtension(magic));
    }

    [Fact]
    public void GuessVideoExtension_DetectsMpegTransportStream()
    {
        var data = new byte[565];
        data[0] = 0x47;
        data[188] = 0x47;
        data[376] = 0x47;
        data[564] = 0x47;

        Assert.Equal(".ts", VideoSignatureUtil.GuessVideoExtension(data));
    }

    [Fact]
    public void GuessVideoExtension_DetectsMpegTransportStreamWithoutFourthSyncByte()
    {
        var data = new byte[400];
        data[0] = 0x47;
        data[188] = 0x47;
        data[376] = 0x47;

        Assert.Equal(".ts", VideoSignatureUtil.GuessVideoExtension(data));
    }

    [Theory]
    [InlineData(nameof(Rar4Magic))]
    [InlineData(nameof(Par2Magic))]
    public void GuessVideoExtension_ReturnsNullForNonVideoMagic(string magicName)
    {
        var magic = magicName switch
        {
            nameof(Rar4Magic) => Rar4Magic,
            nameof(Par2Magic) => Par2Magic,
            _ => throw new ArgumentOutOfRangeException(nameof(magicName)),
        };

        Assert.Null(VideoSignatureUtil.GuessVideoExtension(magic));
    }

    [Fact]
    public void GuessVideoExtension_ReturnsNullForRandomBytes()
    {
        Assert.Null(VideoSignatureUtil.GuessVideoExtension([0xDE, 0xAD, 0xBE, 0xEF]));
    }

    [Fact]
    public void GuessVideoExtension_ReturnsNullForTruncatedMp4()
    {
        Assert.Null(VideoSignatureUtil.GuessVideoExtension(Mp4Magic.AsSpan(0, 8)));
    }

    [Fact]
    public void LooksLikeArchiveMagic_DetectsRarAndSevenZip()
    {
        Assert.True(VideoSignatureUtil.LooksLikeArchiveMagic(Rar4Magic));
        Assert.True(VideoSignatureUtil.LooksLikeArchiveMagic([0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00]));
        Assert.False(VideoSignatureUtil.LooksLikeArchiveMagic(EbmlMagic));
    }

    [Fact]
    public void SniffMemberFromFirst16KB_SkipsEncryptedAndArchiveMagic()
    {
        var first16KB = new byte[VideoSignatureUtil.First16KBLength];
        EbmlMagic.CopyTo(first16KB, 100);
        Rar4Magic.CopyTo(first16KB, 200);

        Assert.Equal(".mkv", VideoSignatureUtil.SniffMemberFromFirst16KB(first16KB, 100, encrypted: false));
        Assert.Null(VideoSignatureUtil.SniffMemberFromFirst16KB(first16KB, 200, encrypted: false));
        Assert.Null(VideoSignatureUtil.SniffMemberFromFirst16KB(first16KB, 100, encrypted: true));
        Assert.Null(VideoSignatureUtil.SniffMemberFromFirst16KB(first16KB, 16_384, encrypted: false));
    }

    [Fact]
    public void SniffMemberFromFirst16KB_DetectsSignatureNearEndOfBuffer()
    {
        var first16KB = new byte[VideoSignatureUtil.First16KBLength];
        var offset = VideoSignatureUtil.First16KBLength - 8;
        EbmlMagic.CopyTo(first16KB, offset);

        Assert.Equal(".mkv", VideoSignatureUtil.SniffMemberFromFirst16KB(first16KB, offset, encrypted: false));
    }
}
