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
                posted(item) ?? DateTimeOffset.MinValue);
        });

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

    private sealed record Ranked<T>(
        T Item,
        int Resolution,
        int Source,
        int Grabs,
        long Size,
        DateTimeOffset Posted);
}
