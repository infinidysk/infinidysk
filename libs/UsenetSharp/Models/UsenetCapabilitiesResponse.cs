using UsenetSharp.Models;

namespace UsenetSharp.Models;

/// <summary>
/// Response to the CAPABILITIES command (RFC 3977 §5.2).
/// </summary>
public sealed record UsenetCapabilitiesResponse : UsenetResponse
{
    /// <summary>
    /// Capability labels in wire order, including VERSION and unknown labels.
    /// </summary>
    public required IReadOnlyList<string> Capabilities { get; init; }
}
