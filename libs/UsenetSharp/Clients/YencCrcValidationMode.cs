namespace UsenetSharp.Clients;

/// <summary>
/// Controls how decoded yEnc trailers are validated against CRC32 fields.
/// </summary>
public enum YencCrcValidationMode
{
    /// <summary>Never validate (legacy default).</summary>
    Off,

    /// <summary>
    /// Validate when the trailer carries a CRC; tolerate absent CRCs (yEnc 1.3 SHOULD).
    /// </summary>
    WhenPresent,

    /// <summary>Require and validate a CRC; absent CRC fails the stream.</summary>
    Require,
}
