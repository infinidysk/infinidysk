using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using NzbWebDAV.Api.Controllers;
using NzbWebDAV.Api.SabControllers;

namespace NzbWebDAV.Api.Errors;

public sealed class ApiErrorContractFilter : IAsyncResultFilter
{
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        Rewrite(context);
        await next().ConfigureAwait(false);
    }

    private static void Rewrite(ResultExecutingContext context)
    {
        if (context.Result is not ObjectResult objectResult)
            return;

        var httpContext = context.HttpContext;
        if (objectResult.Value is BaseApiResponse { Status: false } admin
            && ApiRequestClassifier.IsAdminApi(httpContext))
        {
            var status = objectResult.StatusCode ?? StatusCodes.Status400BadRequest;
            var problem = ApiProblemDetailsFactory.FromStatus(httpContext, status, admin.Error);
            context.Result = new ProblemJsonResult(
                problem.Status ?? status,
                ApiProblemDetailsFactory.ToWritablePayload(problem));
            return;
        }

        if (objectResult.Value is SabBaseResponse { Status: false } sab
            && ApiRequestClassifier.IsSabApi(httpContext))
        {
            var status = objectResult.StatusCode ?? StatusCodes.Status400BadRequest;
            sab.Problem ??= ApiProblemDetailsFactory.ToWritablePayload(
                ApiProblemDetailsFactory.FromStatus(httpContext, status, sab.Error));
        }
    }
}
