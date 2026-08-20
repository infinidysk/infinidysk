using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.Errors;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.SabControllers.MoveInQueue;

public class MoveInQueueRequest
{
    public List<Guid> NzoIds { get; init; } = [];
    public bool MoveToTop { get; init; }
    public CancellationToken CancellationToken { get; init; }

    public static async Task<MoveInQueueRequest> New(HttpContext httpContext)
    {
        var cancellationToken = SigtermUtil.GetCancellationToken();
        var queryIds = ParseNzoIds(httpContext);
        var bodyIds = await SabJsonNzoIds.ReadAsync(httpContext, cancellationToken).ConfigureAwait(false);
        var errors = new ValidationErrors();
        var moveToTop = TryIsMoveToTop(httpContext.GetRequestParam("value2"), errors);

        errors.ThrowIfAny();

        return new MoveInQueueRequest
        {
            NzoIds = queryIds.Concat(bodyIds).Distinct().ToList(),
            MoveToTop = moveToTop,
            CancellationToken = cancellationToken
        };
    }

    /// <summary>
    /// SABnzbd uses absolute index 0 or the token "top" for move-to-top.
    /// Missing value2 defaults to top so a simple move call is enough for our UI.
    /// </summary>
    internal static bool IsMoveToTop(string? position)
    {
        var errors = new ValidationErrors();
        var result = TryIsMoveToTop(position, errors);
        errors.ThrowIfAny();
        return result;
    }

    private static bool TryIsMoveToTop(string? position, ValidationErrors errors)
    {
        if (string.IsNullOrWhiteSpace(position))
            return true;

        if (position.Equals("top", StringComparison.OrdinalIgnoreCase))
            return true;

        if (int.TryParse(position, out var index) && index == 0)
            return true;

        errors.Add("value2", "Only move-to-top is supported (value2=0 or value2=top).");
        return false;
    }

    private static List<Guid> ParseNzoIds(HttpContext httpContext)
    {
        var ids = new List<Guid>();
        var seen = new HashSet<Guid>();
        foreach (var token in httpContext.GetQueryParamValues("value")
                     .SelectMany(value => value.Split(',', StringSplitOptions.TrimEntries |
                                                          StringSplitOptions.RemoveEmptyEntries)))
        {
            if (Guid.TryParse(token, out var id) && seen.Add(id))
                ids.Add(id);
        }

        return ids;
    }
}
