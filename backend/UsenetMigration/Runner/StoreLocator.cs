using NzbWebDAV.UsenetMigration.Source;

namespace NzbWebDAV.UsenetMigration.Runner;

/// <summary>
/// Resolves a release's <c>store_ref</c> to an on-disk <c>.nzbz</c> path. The ref
/// is authoritative and normally read directly; when it was authored on another
/// host (a copied library), it is remapped under the configured store root by its
/// <c>.nzbs/…</c> suffix. Returns null when no readable file is found (⇒
/// <c>store_missing</c>).
/// </summary>
public static class StoreLocator
{
    public static string? Resolve(string storeRef, string? storeRoot)
    {
        if (File.Exists(storeRef)) return storeRef;
        return ResolveUnderRoot(storeRef, storeRoot);
    }

    /// <summary>
    /// Locates a v1 <c>source_nzb_path</c>: try the recorded path, then with
    /// <c>.gz</c> appended, then remapped under <paramref name="storeRoot"/> via
    /// the shared <c>/.nzbs/</c> suffix logic.
    /// </summary>
    public static string? ResolveSourceNzb(string? sourceNzbPath, string? storeRoot)
    {
        if (string.IsNullOrWhiteSpace(sourceNzbPath))
            return null;

        if (File.Exists(sourceNzbPath))
            return sourceNzbPath;

        var withGz = AppendGzIfNeeded(sourceNzbPath);
        if (withGz is not null && File.Exists(withGz))
            return withGz;

        var remapped = ResolveUnderRoot(sourceNzbPath, storeRoot);
        if (remapped is not null)
            return remapped;

        if (withGz is not null)
            return ResolveUnderRoot(withGz, storeRoot);

        return null;
    }

    /// <summary>
    /// Remaps an absolute path under <paramref name="storeRoot"/> using its
    /// <c>.nzbs/…</c> suffix when the original host path is not readable.
    /// </summary>
    public static string? ResolveUnderRoot(string path, string? storeRoot)
    {
        if (string.IsNullOrEmpty(storeRoot))
            return null;

        var normalised = path.Replace('\\', '/');
        var marker = "/" + StorePathParser.NzbsDirName + "/";
        var idx = normalised.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
            return null;

        var suffix = normalised[(idx + 1)..].Replace('/', Path.DirectorySeparatorChar);
        var candidate = Path.Combine(storeRoot, suffix);
        return File.Exists(candidate) ? candidate : null;
    }

    private static string? AppendGzIfNeeded(string path)
    {
        if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            return null;
        return path + ".gz";
    }
}
