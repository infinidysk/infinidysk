using System.Globalization;
using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.Errors;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Api.Controllers.BrowseFiles;

public sealed class BrowseFilesRequest
{
    public const int DefaultLimit = 100;
    public const int MaximumLimit = 200;
    public const int MaximumQueryLength = 256;

    public string ScopePath { get; }
    public string ParentPath { get; }
    public string Mode { get; }
    public string? Q { get; }
    public string[] Health { get; }
    public string Schedule { get; }
    public string Library { get; }
    public string? Category { get; }
    public string? Indexer { get; }
    public DavItem.ItemSubType? SubType { get; }
    public HealthCheckResult.RepairAction? RepairAction { get; }
    public bool? HasNzb { get; }
    public long? MinSize { get; }
    public long? MaxSize { get; }
    public DateTimeOffset? AddedAfter { get; }
    public DateTimeOffset? AddedBefore { get; }
    public DateTimeOffset? PostedAfter { get; }
    public DateTimeOffset? PostedBefore { get; }
    public DateTimeOffset? CheckedAfter { get; }
    public DateTimeOffset? CheckedBefore { get; }
    public DateTimeOffset? PlayedAfter { get; }
    public DateTimeOffset? PlayedBefore { get; }
    public int Offset { get; }
    public int Limit { get; }
    public string Sort { get; }
    public string Direction { get; }
    public bool HasFilters => Q is not null || Health.Length > 0 || Schedule != "all" || Library != "all" ||
        Category is not null || Indexer is not null || SubType is not null || RepairAction is not null ||
        HasNzb is not null || MinSize is not null || MaxSize is not null || AddedAfter is not null ||
        AddedBefore is not null || PostedAfter is not null || PostedBefore is not null ||
        CheckedAfter is not null || CheckedBefore is not null || PlayedAfter is not null || PlayedBefore is not null;

    public BrowseFilesRequest(HttpContext context)
    {
        var errors = new ValidationErrors();
        string? Read(string name)
        {
            if (context.GetQueryParamValues(name).Count() > 1)
                errors.Add(name, "Expected a single value.");
            return context.GetQueryParam(name);
        }

        string Choice(string name, string fallback, params string[] allowed)
        {
            var value = Read(name) ?? fallback;
            if (!allowed.Contains(value, StringComparer.Ordinal)) errors.Add(name, "Invalid value.");
            return value;
        }

        string? Text(string name, int maximum, bool trim = false)
        {
            var value = Read(name);
            if (trim) value = value?.Trim();
            if (value?.Length > maximum) errors.Add(name, $"Maximum length is {maximum}.");
            return string.IsNullOrEmpty(value) ? null : value;
        }

        long? Integer(string name, long maximum = long.MaxValue)
        {
            var value = Read(name);
            if (value is null) return null;
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) &&
                number >= 0 && number <= maximum) return number;
            errors.Add(name, "Expected a nonnegative integer within range.");
            return null;
        }

        string Path(string name, string fallback)
        {
            try { return NormalizeContentPath(Read(name) ?? fallback, name); }
            catch (BadHttpRequestException exception) { errors.Add(name, exception.Message); return fallback; }
        }

        ScopePath = Path("scopePath", "/content");
        ParentPath = Path("parentPath", ScopePath);
        Mode = Choice("mode", "tree", "tree", "list");
        if (Mode == "tree" && !IsWithinScope(ParentPath, ScopePath)) errors.Add("parentPath", "Must be inside scope.");
        Q = Text("q", MaximumQueryLength, true);
        Health = (Read("health") ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (Health.Any(value => value is not ("healthy" or "degraded" or "needs-attention" or "unknown")))
            errors.Add("health", "Invalid health value.");
        Schedule = Choice("schedule", "all", "all", "due", "scheduled", "recheck-queued", "repair-pending", "never-scheduled", "checking");
        Library = Choice("library", "all", "all", "in-library", "not-in-library", "unknown", "not-configured");
        Category = Text("category", 255);
        Indexer = Text("indexer", 255);
        var subType = Integer("subType", int.MaxValue);
        if (subType is not null && subType is not (201 or 202 or 203)) errors.Add("subType", "Invalid storage kind.");
        SubType = subType is null ? null : (DavItem.ItemSubType)subType;
        var repair = Integer("repairAction", int.MaxValue);
        if (repair is not null && !Enum.IsDefined((HealthCheckResult.RepairAction)(int)repair)) errors.Add("repairAction", "Invalid repair action.");
        RepairAction = repair is null ? null : (HealthCheckResult.RepairAction)(int)repair;
        var nzb = Read("hasNzb");
        if (nzb is not null && nzb is not ("true" or "false")) errors.Add("hasNzb", "Expected true or false.");
        HasNzb = nzb is null ? null : nzb == "true";
        MinSize = Integer("minSize");
        MaxSize = Integer("maxSize");
        if (MinSize > MaxSize) errors.Add("maxSize", "Must not be smaller than minSize.");
        AddedAfter = ReadInstant(Read("addedAfter"), "addedAfter", errors);
        AddedBefore = ReadInstant(Read("addedBefore"), "addedBefore", errors);
        PostedAfter = ReadInstant(Read("postedAfter"), "postedAfter", errors);
        PostedBefore = ReadInstant(Read("postedBefore"), "postedBefore", errors);
        CheckedAfter = ReadInstant(Read("checkedAfter"), "checkedAfter", errors);
        CheckedBefore = ReadInstant(Read("checkedBefore"), "checkedBefore", errors);
        PlayedAfter = ReadInstant(Read("playedAfter"), "playedAfter", errors);
        PlayedBefore = ReadInstant(Read("playedBefore"), "playedBefore", errors);
        foreach (var (after, before, field) in new[] {
            (AddedAfter, AddedBefore, "addedBefore"), (PostedAfter, PostedBefore, "postedBefore"),
            (CheckedAfter, CheckedBefore, "checkedBefore"), (PlayedAfter, PlayedBefore, "playedBefore") })
            if (after > before) errors.Add(field, "Must not precede the lower bound.");
        Offset = (int)(Integer("offset", int.MaxValue) ?? 0);
        Limit = (int)(Integer("limit", MaximumLimit) ?? DefaultLimit);
        if (Limit < 1) errors.Add("limit", "Must be between 1 and 200.");
        Sort = Choice("sort", "name", "name", "size", "added", "posted", "last-check", "next-check", "type", "health");
        Direction = Choice("direction", "asc", "asc", "desc");
        errors.ThrowIfAny();
    }

    internal static string NormalizeContentPath(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0', StringComparison.Ordinal))
            throw new BadHttpRequestException($"Invalid {parameter}.");
        var path = value.EndsWith('/') ? value[..^1] : value;
        if (path != "/content" && !path.StartsWith("/content/", StringComparison.Ordinal))
            throw new BadHttpRequestException($"{parameter} must be under /content.");
        if (path.Split('/').Skip(1).Any(segment => segment is "" or "." or ".."))
            throw new BadHttpRequestException($"Invalid {parameter}.");
        return path;
    }

    internal static bool IsWithinScope(string path, string scope) =>
        path == scope || path.StartsWith(scope + "/", StringComparison.Ordinal);

    private static DateTimeOffset? ReadInstant(string? value, string field, ValidationErrors errors)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            errors.Add(field, "Expected integer Unix seconds.");
            return null;
        }
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { errors.Add(field, "Date is out of range."); return null; }
    }
}