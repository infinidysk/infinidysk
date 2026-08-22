using System.Text.RegularExpressions;

namespace NzbWebDAV.Utils;

public readonly record struct ReleaseQualityRanks(int Resolution, int Source)
{
    public static readonly ReleaseQualityRanks Unknown = new(ResolutionUnknown, SourceUnknown);

    public const int Resolution4320 = 5;
    public const int Resolution2160 = 4;
    public const int Resolution1080 = 3;
    public const int Resolution720 = 2;
    public const int ResolutionSd = 1;
    public const int ResolutionUnknown = 0;

    public const int SourceRemux = 6;
    public const int SourceBluRay = 5;
    public const int SourceWebDl = 4;
    public const int SourceWebRip = 3;
    public const int SourceHdtv = 2;
    public const int SourceDvd = 1;
    public const int SourceUnknown = 0;
    public const int SourceCam = -1;
}

public static class ReleaseQuality
{
    private static readonly Regex ResolutionRegex = new(
        @"(?<![A-Za-z0-9])(?:(?<uhd8k>4320p|8k)|(?<uhd>2160p|4k|uhd)|(?<fhd>1080p|1080i)|(?<hd>720p)|(?<sd>576p|480p|sd))(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SourceRegex = new(
        @"(?<![A-Za-z0-9])(?:(?<remux>remux)|(?<bluray>blu[-.]?ray|bdrip|brrip)|(?<webdl>web[-.]?dl)|(?<webrip>webrip)|(?<hdtv>hdtv)|(?<dvd>dvdrip|dvd)|(?<cam>telesync|cam|ts))(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ReleaseQualityRanks Parse(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return ReleaseQualityRanks.Unknown;

        var normalized = title.Replace('_', '.');
        return new ReleaseQualityRanks(ParseResolution(normalized), ParseSource(normalized));
    }

    private static int ParseResolution(string title)
    {
        var match = ResolutionRegex.Match(title);
        if (!match.Success) return ReleaseQualityRanks.ResolutionUnknown;
        if (match.Groups["uhd8k"].Success) return ReleaseQualityRanks.Resolution4320;
        if (match.Groups["uhd"].Success) return ReleaseQualityRanks.Resolution2160;
        if (match.Groups["fhd"].Success) return ReleaseQualityRanks.Resolution1080;
        if (match.Groups["hd"].Success) return ReleaseQualityRanks.Resolution720;
        if (match.Groups["sd"].Success) return ReleaseQualityRanks.ResolutionSd;
        return ReleaseQualityRanks.ResolutionUnknown;
    }

    private static int ParseSource(string title)
    {
        var match = SourceRegex.Match(title);
        if (!match.Success) return ReleaseQualityRanks.SourceUnknown;
        if (match.Groups["remux"].Success) return ReleaseQualityRanks.SourceRemux;
        if (match.Groups["bluray"].Success) return ReleaseQualityRanks.SourceBluRay;
        if (match.Groups["webdl"].Success) return ReleaseQualityRanks.SourceWebDl;
        if (match.Groups["webrip"].Success) return ReleaseQualityRanks.SourceWebRip;
        if (match.Groups["hdtv"].Success) return ReleaseQualityRanks.SourceHdtv;
        if (match.Groups["dvd"].Success) return ReleaseQualityRanks.SourceDvd;
        if (match.Groups["cam"].Success) return ReleaseQualityRanks.SourceCam;
        return ReleaseQualityRanks.SourceUnknown;
    }
}
