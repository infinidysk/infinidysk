using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.Errors;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.Controllers.Authenticate;

public class AuthenticateRequest
{
    public Account.AccountType Type { get; init; }
    public string Username { get; init; }
    public string Password { get; init; }

    public AuthenticateRequest(HttpContext context)
    {
        var errors = new ValidationErrors();
        Username = context.Request.Form["username"].FirstOrDefault()?.ToLowerInvariant() ?? "";
        Password = context.Request.Form["password"].FirstOrDefault() ?? "";
        if (string.IsNullOrEmpty(Username))
            errors.Add("username", "Username is required");
        if (context.Request.Form["password"].FirstOrDefault() is null)
            errors.Add("password", "Password is required");
        if (!Enum.TryParse<Account.AccountType>(context.Request.Form["type"], ignoreCase: true, out var parsedType))
            errors.Add("type", "Invalid account type");
        errors.ThrowIfAny();
        Type = parsedType;
    }
}
