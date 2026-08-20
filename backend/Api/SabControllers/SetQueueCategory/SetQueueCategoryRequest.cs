using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.Errors;
using NzbWebDAV.Api.SabControllers;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.SabControllers.SetQueueCategory;

public class SetQueueCategoryRequest
{
    public List<Guid> NzoIds { get; init; } = [];
    public string Category { get; init; } = null!;
    public CancellationToken CancellationToken { get; init; }

    public static async Task<SetQueueCategoryRequest> New(HttpContext httpContext, ConfigManager configManager)
    {
        var parsed = await SabNzoIdsParser.ParseAsync(httpContext, SigtermUtil.GetCancellationToken())
            .ConfigureAwait(false);
        var category = httpContext.GetRequestParam("cat")
                       ?? httpContext.GetRequestParam("category");
        var errors = new ValidationErrors();
        if (string.IsNullOrWhiteSpace(category))
            errors.Add("cat", "Missing cat/category param.");
        else if (!configManager.GetApiCategories().Contains(category, StringComparer.OrdinalIgnoreCase))
            errors.Add("cat", "Invalid category.");
        errors.ThrowIfAny();

        return new SetQueueCategoryRequest
        {
            NzoIds = parsed.NzoIds,
            Category = category!,
            CancellationToken = SigtermUtil.GetCancellationToken(),
        };
    }
}
