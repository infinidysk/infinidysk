using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace NzbWebDAV.Api.Errors;

public static class ApiProblemResponse
{
    public static Task WriteAsync(
        HttpContext context,
        int status,
        object payload,
        string contentType)
    {
        context.Response.StatusCode = status;
        RequestCorrelation.ApplyResponseHeader(context);
        context.Response.ContentType = contentType;
        return JsonSerializer.SerializeAsync(
            context.Response.Body,
            payload,
            ApiProblemDetailsFactory.JsonOptions,
            context.RequestAborted);
    }
}

public sealed class ProblemJsonResult(int status, object payload) : IActionResult
{
    public Task ExecuteResultAsync(ActionContext context) =>
        ApiProblemResponse.WriteAsync(
            context.HttpContext,
            status,
            payload,
            ApiProblemDetailsFactory.ProblemContentType);
}
