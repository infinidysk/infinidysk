namespace NzbWebDAV.Config;

/// <summary>
/// Byte ceilings for untrusted Newznab XML and Watchtower list-source JSON/text.
/// These are parser-visible <see cref="HttpContent"/> bytes after the HTTP handler
/// (currently no automatic decompression). They are not NZB download limits.
/// </summary>
public static class ExternalMetadataResponseLimits
{
    /// <summary>
    /// Default Newznab caps/search page ceiling. Typical 100-result extended RSS
    /// is tens to hundreds of KiB; 4 MiB leaves headroom for verbose indexers without
    /// approaching the 50 MiB NZB download ceiling.
    /// </summary>
    public const long NewznabDefaultMaxResponseBytes = 4L * 1024 * 1024;

    /// <summary>
    /// Default Watchtower manifest/catalog/list ceiling. List sync is sequential;
    /// 8 MiB covers large JSON catalogs and URL lists with headroom.
    /// </summary>
    public const long WatchtowerDefaultMaxResponseBytes = 8L * 1024 * 1024;

    /// <summary>
    /// Operator-facing hard clamp for both families. Below the 50 MiB NZB
    /// per-response ceiling; concurrent Newznab parses must still fit a small container.
    /// </summary>
    public const long HardMaxResponseBytes = 16L * 1024 * 1024;
}
