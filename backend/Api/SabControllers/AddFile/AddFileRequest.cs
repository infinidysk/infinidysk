using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.Errors;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Queue;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.SabControllers.AddFile;

public class AddFileRequest()
{
    /// <summary>
    /// Optional caller-assigned SAB job id. Ordinary uploads leave this null;
    /// durable in-process workflows can persist the id before crossing the queue
    /// mutation boundary and safely recover after a crash.
    /// </summary>
    public Guid? NzoId { get; init; }

    /// <summary>
    /// Whether an existing queue item with the same category and filename may be
    /// replaced. SAB uploads retain the historical replacement behavior; durable
    /// migration submissions disable it so recovery never evicts an ambiguous job.
    /// </summary>
    public bool ReplaceExistingQueueItem { get; init; } = true;

    public string FileName { get; init; } = null!;
    public string? ContentType { get; init; }
    public Stream NzbFileStream { get; init; } = null!;
    public string Category { get; init; } = null!;
    public QueueItem.PriorityOption Priority { get; init; }
    public QueueItem.PostProcessingOption PostProcessing { get; init; }
    public DateTime? PauseUntil { get; init; }
    public string? IndexerName { get; init; }
    public string? ContentGroupKey { get; init; }
    public CancellationToken CancellationToken { get; init; }

    public static Task<AddFileRequest> New(HttpContext context, ConfigManager configManager)
    {
        var errors = new ValidationErrors();
        var file =
            context.Request.HasFormContentType
                ? context.Request.Form.Files["nzbFile"] ?? context.Request.Form.Files["name"]
                : null;
        if (file is null)
            errors.Add("nzbFile", "Invalid nzbFile/name param");

        var fileName = TryResolveFileName(
            context.GetRequestParam("nzbname"),
            file?.FileName,
            errors);

        if (!TryMapPriorityOption(context.GetRequestParam("priority"), out var priority))
            errors.Add("priority", "Invalid priority");
        if (!TryMapPostProcessingOption(context.GetRequestParam("pp"), out var postProcessing))
            errors.Add("pp", "Invalid pp param");

        errors.ThrowIfAny();

        return Task.FromResult(new AddFileRequest()
        {
            FileName = fileName!,
            ContentType = file!.ContentType,
            NzbFileStream = file.OpenReadStream(),
            Category = SabCategoryResolver.GetCategory(context, configManager)
                       ?? configManager.GetManualUploadCategory(),
            Priority = priority,
            PostProcessing = postProcessing,
            CancellationToken = context.RequestAborted
        });
    }

    internal NzbSubmissionRequest ToSubmissionRequest() => new()
    {
        NzoId = NzoId,
        ReplaceExistingQueueItem = ReplaceExistingQueueItem,
        FileName = FileName,
        NzbFileStream = NzbFileStream,
        Category = Category,
        Priority = Priority,
        PostProcessing = PostProcessing,
        PauseUntil = PauseUntil,
        IndexerName = IndexerName,
        ContentGroupKey = ContentGroupKey,
        CancellationToken = CancellationToken,
    };

    /// <summary>
    /// Resolve the NZB filename from an optional SAB <c>nzbname</c> param and the uploaded file name.
    /// </summary>
    internal static string ResolveFileName(string? nzbName, string? formFileName)
    {
        var errors = new ValidationErrors();
        var fileName = TryResolveFileName(nzbName, formFileName, errors);
        errors.ThrowIfAny();
        return fileName!;
    }

    internal static string? TryResolveFileName(string? nzbName, string? formFileName, ValidationErrors errors)
    {
        var fileName = !string.IsNullOrWhiteSpace(nzbName) ? nzbName : formFileName;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            errors.Add("nzbname", "NZB filename could not be determined.");
            return null;
        }

        var normalized = NzbStreamUtil.NormalizeFileName(fileName);
        if (normalized.Contains('/', StringComparison.Ordinal)
            || normalized.Contains('\\', StringComparison.Ordinal)
            || normalized.Contains('\0', StringComparison.Ordinal)
            || normalized is "." or ".."
            || Path.IsPathRooted(normalized))
        {
            errors.Add("nzbname", "NZB filename must be a single path segment.");
            return null;
        }

        if (normalized.Length > NzbInputLimits.Default.MaxNameLength)
        {
            errors.Add("nzbname", "NZB filename exceeds the maximum name length.");
            return null;
        }

        return normalized;
    }

    internal static QueueItem.PriorityOption MapPriorityOption(string? priority)
    {
        if (TryMapPriorityOption(priority, out var option))
            return option;
        var errors = new ValidationErrors();
        errors.Add("priority", "Invalid priority");
        errors.ThrowIfAny();
        return default;
    }

    internal static bool TryMapPriorityOption(string? priority, out QueueItem.PriorityOption option)
    {
        option = priority switch
        {
            "-100" => QueueItem.PriorityOption.Normal,
            "-3" => QueueItem.PriorityOption.Duplicate,
            "-2" => QueueItem.PriorityOption.Paused,
            "-1" => QueueItem.PriorityOption.Low,
            "0" => QueueItem.PriorityOption.Normal,
            "1" => QueueItem.PriorityOption.High,
            "2" => QueueItem.PriorityOption.Force,
            null => QueueItem.PriorityOption.Normal,
            _ => (QueueItem.PriorityOption)int.MinValue,
        };
        return option != (QueueItem.PriorityOption)int.MinValue;
    }

    protected static QueueItem.PostProcessingOption MapPostProcessingOption(string? postProcessing)
    {
        if (TryMapPostProcessingOption(postProcessing, out var option))
            return option;
        var errors = new ValidationErrors();
        errors.Add("pp", "Invalid pp param");
        errors.ThrowIfAny();
        return default;
    }

    internal static bool TryMapPostProcessingOption(string? postProcessing, out QueueItem.PostProcessingOption option)
    {
        option = postProcessing switch
        {
            "-1" => QueueItem.PostProcessingOption.None,
            "0" => QueueItem.PostProcessingOption.None,
            "1" => QueueItem.PostProcessingOption.Repair,
            "2" => QueueItem.PostProcessingOption.RepairUnpack,
            "3" => QueueItem.PostProcessingOption.RepairUnpackDelete,
            null => QueueItem.PostProcessingOption.None,
            _ => (QueueItem.PostProcessingOption)int.MinValue,
        };
        return option != (QueueItem.PostProcessingOption)int.MinValue;
    }
}
