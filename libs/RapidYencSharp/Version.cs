namespace RapidYencSharp;

/// <summary>
/// Provides version information for the rapidyenc library
/// </summary>
public static class Version
{
    /// <summary>
    /// Gets the version of the rapidyenc native library
    /// </summary>
    /// <returns>The version in 0xMMmmpp format, where MM=major version, mm=minor version, pp=patch version</returns>
    public static int GetVersion()
    {
        NativeMethods.EnsureResolverRegistered();
        return NativeMethods.rapidyenc_version();
    }

    /// <summary>
    /// Gets the major version number
    /// </summary>
    public static int Major => (GetVersion() >> 16) & 0xFF;

    /// <summary>
    /// Gets the minor version number
    /// </summary>
    public static int Minor => (GetVersion() >> 8) & 0xFF;

    /// <summary>
    /// Gets the patch version number
    /// </summary>
    public static int Patch => GetVersion() & 0xFF;
}
