using SharpCompress.Common.Rar.Headers;

namespace SharpCompress.Common.Rar;

/// <summary>
/// Public-facing adapter exposing RAR encryption parameters (RAR3 <c>R4Salt</c> or
/// RAR5 <see cref="Rar5CryptoInfo"/>) as an <see cref="IRarCryptoInfo"/> without leaking internals.
/// </summary>
internal sealed class RarCryptoInfoView : IRarCryptoInfo
{
    private const int Rar3KdfIterations = 1 << 18;

    private RarCryptoInfoView(
        bool isRar5,
        byte[] salt,
        byte[]? initV,
        int kdfIterations,
        bool usePasswordCheck,
        byte[]? passwordCheck
    )
    {
        IsRar5 = isRar5;
        Salt = salt;
        InitV = initV;
        KdfIterations = kdfIterations;
        UsePasswordCheck = usePasswordCheck;
        PasswordCheck = passwordCheck;
    }

    public static RarCryptoInfoView ForRar3(byte[] salt) =>
        new(
            isRar5: false,
            salt: salt,
            initV: null,
            kdfIterations: Rar3KdfIterations,
            usePasswordCheck: false,
            passwordCheck: null
        );

    public static RarCryptoInfoView ForRar5(Rar5CryptoInfo cryptoInfo) =>
        new(
            isRar5: true,
            salt: cryptoInfo.Salt,
            initV: cryptoInfo.InitV,
            kdfIterations: 1 << cryptoInfo.LG2Count,
            usePasswordCheck: cryptoInfo.UsePswCheck,
            passwordCheck: cryptoInfo.UsePswCheck ? cryptoInfo.PswCheck : null
        );

    public bool IsRar5 { get; }
    public byte[] Salt { get; }
    public byte[]? InitV { get; }
    public int KdfIterations { get; }
    public bool UsePasswordCheck { get; }
    public byte[]? PasswordCheck { get; }
}
