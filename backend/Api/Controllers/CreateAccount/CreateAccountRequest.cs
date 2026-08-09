using Microsoft.AspNetCore.Http;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.Controllers.CreateAccount;

public class CreateAccountRequest
{
    public Account.AccountType Type { get; init; }
    public string Username { get; init; }
    public string Password { get; init; }

    public CreateAccountRequest(HttpContext context)
    {
        Username = context.Request.Form["username"].FirstOrDefault()?.ToLowerInvariant() ??
            throw new BadHttpRequestException("Username is required");

        Password = context.Request.Form["password"].FirstOrDefault() ??
            throw new BadHttpRequestException("Password is required");

        Type = !Enum.TryParse<Account.AccountType>(context.Request.Form["type"], ignoreCase: true, out var parsedType)
            ? throw new BadHttpRequestException("Invalid account type")
            : parsedType;
    }
}
