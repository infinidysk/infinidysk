namespace SharpCompress.Common.Rar.Headers;

/// <summary>
/// Read-only view of the RAR encryption parameters required to derive an AES key
/// for an encrypted file header without extracting the archive.
/// </summary>
public interface IRarCryptoInfo
{
    /// <summary>True for RAR5 archives; false for RAR3/RAR4.</summary>
    bool IsRar5 { get; }

    /// <summary>Key-derivation salt (RAR3 <c>R4Salt</c> or RAR5 <c>Rar5CryptoInfo.Salt</c>).</summary>
    byte[] Salt { get; }

    /// <summary>AES initialization vector. RAR5 only; <c>null</c> for RAR3/RAR4.</summary>
    byte[]? InitV { get; }

    /// <summary>Number of KDF iterations (RAR5: <c>1 &lt;&lt; LG2Count</c>; RAR3: <c>1 &lt;&lt; 18</c>).</summary>
    int KdfIterations { get; }

    /// <summary>True when a RAR5 password-check value is present and should be validated.</summary>
    bool UsePasswordCheck { get; }

    /// <summary>RAR5 password-check value; <c>null</c> when unavailable.</summary>
    byte[]? PasswordCheck { get; }
}
