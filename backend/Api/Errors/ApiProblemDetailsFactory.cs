using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace NzbWebDAV.Api.Errors;

public static class ApiProblemDetailsFactory
{
    public const string ProblemContentType = "application/problem+json";
    public const string TypePrefix = "https://www.infinidysk.com/problems/";
    public const string InternalErrorDetail =
        "Use the trace ID to find the corresponding server log event.";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static ProblemDetails Create(
        HttpContext context,
        int status,
        string typeSuffix,
        string title,
        string? detail)
    {
        var problem = new ProblemDetails
        {
            Type = TypePrefix + typeSuffix,
            Title = title,
            Status = status,
            Detail = detail,
        };
        problem.Extensions["traceId"] = RequestCorrelation.Resolve(context);
        return problem;
    }

    public static ValidationProblemDetails Validation(
        HttpContext context,
        IReadOnlyDictionary<string, string[]> errors,
        string? detail = null)
    {
        var problem = new ValidationProblemDetails(errors.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value))
        {
            Type = TypePrefix + "validation",
            Title = "One or more validation errors occurred.",
            Status = StatusCodes.Status400BadRequest,
            Detail = detail ?? "One or more validation errors occurred.",
        };
        problem.Extensions["traceId"] = RequestCorrelation.Resolve(context);
        return problem;
    }

    public static ProblemDetails FromException(HttpContext context, Exception exception)
    {
        return exception switch
        {
            ApiValidationException validation => Validation(context, validation.Errors, validation.Message),
            BadHttpRequestException bad => Create(
                context,
                bad.StatusCode is >= 400 and < 600 ? bad.StatusCode : StatusCodes.Status400BadRequest,
                "bad-request",
                "Bad Request",
                bad.Message),
            ArgumentException argument => Create(
                context,
                StatusCodes.Status400BadRequest,
                "bad-request",
                "Bad Request",
                argument.Message),
            UnauthorizedAccessException unauthorized => Create(
                context,
                StatusCodes.Status401Unauthorized,
                "unauthorized",
                "Unauthorized",
                unauthorized.Message),
            _ => Create(
                context,
                StatusCodes.Status500InternalServerError,
                "internal-error",
                "An unexpected server error occurred.",
                InternalErrorDetail),
        };
    }

    public static ProblemDetails FromStatus(HttpContext context, int status, string? detail)
    {
        var (suffix, title, sanitizedDetail) = status switch
        {
            StatusCodes.Status400BadRequest => ("bad-request", "Bad Request", detail),
            StatusCodes.Status401Unauthorized => ("unauthorized", "Unauthorized", detail),
            StatusCodes.Status403Forbidden => ("forbidden", "Forbidden", detail),
            StatusCodes.Status404NotFound => ("not-found", "Not Found", detail),
            StatusCodes.Status409Conflict => ("conflict", "Conflict", detail),
            StatusCodes.Status500InternalServerError => ("internal-error", "An unexpected server error occurred.", InternalErrorDetail),
            _ => ("error", "Request failed", detail),
        };
        return Create(context, status, suffix, title, sanitizedDetail);
    }

    public static Dictionary<string, object?> ToWritablePayload(ProblemDetails problem)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = problem.Type,
            ["title"] = problem.Title,
            ["status"] = problem.Status,
            ["detail"] = problem.Detail,
        };
        if (problem.Extensions.TryGetValue("traceId", out var traceId))
            payload["traceId"] = traceId;
        if (problem is ValidationProblemDetails validation)
            payload["errors"] = validation.Errors;
        return payload;
    }
}
