using System;

namespace SharpCompress.Common.Rar;

/// <summary>
/// Thrown when RAR header enumeration fails. Prefer this over raw BCL exceptions
/// for consumer classification of truncation vs corruption.
/// </summary>
public sealed class RarHeaderReadException : SharpCompressException
{
    public RarHeaderReadException(string message, bool truncated)
        : base(message)
    {
        Truncated = truncated;
    }

    public RarHeaderReadException(string message, bool truncated, Exception inner)
        : base(message, inner)
    {
        Truncated = truncated;
    }

    /// <summary>
    /// True when the stream ended before a complete header could be read
    /// (includes signature-scan EOF, mid-header EOF, and seek-past-end during
    /// packed-data skip). False for CRC mismatch, unknown header codes, missing
    /// signature after a full scan, or other corruption / unsupported format.
    /// </summary>
    public bool Truncated { get; }
}
