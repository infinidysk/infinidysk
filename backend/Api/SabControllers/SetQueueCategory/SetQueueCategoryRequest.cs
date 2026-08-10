using Microsoft.AspNetCore.Http;
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
        if (string.IsNullOrWhiteSpace(category))
            throw new BadHttpRequestException("Missing cat/category param.");

        var allowed = configManager.GetApiCategories();
        if (!allowed.Contains(category, StringComparer.OrdinalIgnoreCase))
            throw new BadHttpRequestException("Invalid category.");

        return new SetQueueCategoryRequest
        {
            NzoIds = parsed.NzoIds,
            Category = category,
            CancellationToken = SigtermUtil.GetCancellationToken(),
        };
    }
}
