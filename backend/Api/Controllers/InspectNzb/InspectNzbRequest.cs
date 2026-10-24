using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.Errors;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Queue.Inspection;

namespace NzbWebDAV.Api.Controllers.InspectNzb;

/// <summary>
/// <c>multipart/form-data</c> with <c>nzbFile</c> (required), and optional <c>password</c>
/// and <c>name</c> (release name used for planned mount names). The password is read
/// from the form body only, never from the query string.
/// </summary>
internal sealed class InspectNzbRequest
{
    public required byte[] NzbBytes { get; init; }
    public string? Password { get; init; }
    public string? Name { get; init; }

    public static async Task<InspectNzbRequest> ReadAsync(HttpContext context, NzbInspectionLimits limits)
    {
        var errors = new ValidationErrors();
        if (!context.Request.HasFormContentType)
        {
            errors.Add("nzbFile", "Send the NZB as the multipart/form-data field nzbFile.");
            errors.ThrowIfAny();
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
        var file = form.Files["nzbFile"];
        if (file is null)
            errors.Add("nzbFile", "Missing nzbFile.");
        else if (file.Length > limits.MaxNzbBytes)
            errors.Add("nzbFile", "The NZB document exceeds the maximum allowed size.");
        errors.ThrowIfAny();

        using var buffer = new MemoryStream(checked((int)file!.Length));
        await using (var input = file.OpenReadStream())
            await input.CopyToAsync(buffer, context.RequestAborted).ConfigureAwait(false);
        var bytes = buffer.ToArray();

        using (var validation = new MemoryStream(bytes, writable: false))
            NzbInputValidator.ValidateAndSumSegmentBytes(validation, NzbInputLimits.Default, context.RequestAborted);

        return new InspectNzbRequest
        {
            NzbBytes = bytes,
            Password = EmptyToNull(form["password"].ToString()),
            Name = EmptyToNull(form["name"].ToString()),
        };
    }

    private static string? EmptyToNull(string value) => string.IsNullOrEmpty(value) ? null : value;
}
