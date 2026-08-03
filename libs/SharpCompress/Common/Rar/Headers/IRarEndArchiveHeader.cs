namespace SharpCompress.Common.Rar.Headers;

/// <summary>
/// Read-only view of a RAR end-of-archive header.
/// </summary>
public interface IRarEndArchiveHeader : IRarHeader
{
    /// <summary>Volume number recorded in the end header, or <c>null</c> when absent.</summary>
    int? VolumeNumber { get; }
}
