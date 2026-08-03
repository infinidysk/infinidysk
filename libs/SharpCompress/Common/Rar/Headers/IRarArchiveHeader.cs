namespace SharpCompress.Common.Rar.Headers;

/// <summary>
/// Read-only view of a RAR archive (main) header.
/// </summary>
public interface IRarArchiveHeader : IRarHeader
{
    /// <summary>Volume number, normalized to <see cref="int"/> across RAR4/RAR5; <c>null</c> when absent.</summary>
    int? VolumeNumber { get; }

    /// <summary>True when this is the first volume of a (potentially multi-volume) archive.</summary>
    bool IsFirstVolume { get; }

    /// <summary>True when the archive is part of a multi-volume set.</summary>
    bool IsVolume { get; }

    /// <summary>True when the archive uses solid compression.</summary>
    bool IsSolid { get; }
}
