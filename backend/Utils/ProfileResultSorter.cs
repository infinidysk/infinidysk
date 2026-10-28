using NzbWebDAV.Config;

namespace NzbWebDAV.Utils;

public static class ProfileResultSorter
{
    public static List<T> Sort<T>(
        IEnumerable<T> items,
        Func<T, string?> title,
        Func<T, int?> grabs,
        Func<T, long> size,
        Func<T, DateTimeOffset?> posted,
        ProfileConfig.QualitySortMode qualitySort,
        bool preferDownloaded)
    {
        var ranked = items.Select(item =>
        {
            var quality = qualitySort == ProfileConfig.QualitySortMode.Off
                ? ReleaseQualityRanks.Unknown
                : ReleaseQuality.Parse(title(item));
            return new Ranked<T>(
                item,
                qualitySort == ProfileConfig.QualitySortMode.Off ? 0 : quality.Resolution,
                qualitySort == ProfileConfig.QualitySortMode.ResolutionAndSource ? quality.Source : 0,
                grabs(item) ?? -1,
                size(item),
                posted(item) ?? DateTimeOffset.MinValue,
                quality.Codec,
                quality.Source == ReleaseQualityRanks.SourceRemux,
                WebResolutionRank(quality.Resolution));
        });

        // Catalog mode is its own ordering: a browser streams one file directly, so a
        // compact 1080p H.264 non-REMUX release starts fastest and decodes everywhere.
        if (qualitySort == ProfileConfig.QualitySortMode.Web1080p)
        {
            var web = ranked
                .OrderByDescending(x => x.WebResolution)
                .ThenByDescending(x => x.Codec)
                .ThenBy(x => x.Remux ? 1 : 0);
            if (preferDownloaded)
                web = web.ThenByDescending(x => x.Grabs);
            return web
                .ThenBy(x => x.Size)
                .ThenByDescending(x => x.Posted)
                .Select(x => x.Item)
                .ToList();
        }

        var ordered = qualitySort == ProfileConfig.QualitySortMode.Off
            ? ranked.OrderByDescending(_ => 0)
            : ranked.OrderByDescending(x => x.Resolution).ThenByDescending(x => x.Source);

        if (preferDownloaded)
            ordered = ordered.ThenByDescending(x => x.Grabs);

        return ordered
            .ThenByDescending(x => x.Size)
            .ThenByDescending(x => x.Posted)
            .Select(x => x.Item)
            .ToList();
    }

    /// <summary>
    /// Resolution preference for catalog ordering: 1080p, then 2160p, 720p, SD, 4320p,
    /// and last anything the title does not declare.
    /// </summary>
    private static int WebResolutionRank(int resolution) => resolution switch
    {
        ReleaseQualityRanks.Resolution1080 => 5,
        ReleaseQualityRanks.Resolution2160 => 4,
        ReleaseQualityRanks.Resolution720 => 3,
        ReleaseQualityRanks.ResolutionSd => 2,
        ReleaseQualityRanks.Resolution4320 => 1,
        _ => 0,
    };

    private sealed record Ranked<T>(
        T Item,
        int Resolution,
        int Source,
        int Grabs,
        long Size,
        DateTimeOffset Posted,
        int Codec,
        bool Remux,
        int WebResolution);
}
