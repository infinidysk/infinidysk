using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Api.Controllers.CreateAccount;

[ApiController]
[Route("api/create-account")]
public class CreateAccountController(DavDatabaseClient dbClient) : BaseApiController
{
    internal async Task<CreateAccountResponse> CreateAccount(CreateAccountRequest request)
    {
        // Onboarding's own isOnboarding()-then-createAccount() check in the frontend is not
        // race-safe on its own: two concurrent onboarding submissions can both observe "no
        // admin yet" before either insert commits. Re-check here, immediately before the
        // write, and let the DB's own unique filtered index (IX_Accounts_SingleAdmin) be the
        // authoritative backstop against the remaining race window between this check and
        // SaveChangesAsync.
        if (request.Type == Account.AccountType.Admin)
        {
            var adminExists = await dbClient.Ctx.Accounts
                .AnyAsync(a => a.Type == Account.AccountType.Admin, HttpContext.RequestAborted)
                .ConfigureAwait(false);
            if (adminExists)
                throw new BadHttpRequestException("An admin account already exists.");
        }

        var randomSalt = Guid.NewGuid().ToString("N");
        var account = new Account()
        {
            Type = request.Type,
            Username = request.Username,
            RandomSalt = randomSalt,
            PasswordHash = PasswordUtil.Hash(request.Password, randomSalt),
        };
        dbClient.Ctx.Accounts.Add(account);
        try
        {
            await dbClient.Ctx.SaveChangesAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsSingleAdminUniqueViolation(ex))
        {
            // Lost the race against a concurrent onboarding submission - the unique index
            // rejected the insert. Report it the same way as the pre-check above.
            throw new BadHttpRequestException("An admin account already exists.");
        }
        catch (DbUpdateException ex) when (IsAccountPrimaryKeyUniqueViolation(ex))
        {
            throw new BadHttpRequestException("An account with that username already exists.");
        }
        return new CreateAccountResponse() { Status = true };
    }

    internal static bool IsSingleAdminUniqueViolation(DbUpdateException ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is not SqliteException sqlite) continue;
            if (sqlite.SqliteErrorCode is not 19) continue; // SQLITE_CONSTRAINT

            var message = sqlite.Message;
            if (message.Contains("IX_Accounts_SingleAdmin", StringComparison.OrdinalIgnoreCase))
                return true;
            if (message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
                && message.Contains("Accounts.Type", StringComparison.OrdinalIgnoreCase)
                && !message.Contains("Accounts.Username", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    internal static bool IsAccountPrimaryKeyUniqueViolation(DbUpdateException ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is not SqliteException sqlite) continue;
            if (sqlite.SqliteErrorCode is not 19) continue; // SQLITE_CONSTRAINT

            var message = sqlite.Message;
            if (message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
                && message.Contains("Accounts.Type", StringComparison.OrdinalIgnoreCase)
                && message.Contains("Accounts.Username", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    protected override async Task<IActionResult> HandleRequest()
    {
        var request = new CreateAccountRequest(HttpContext);
        var response = await CreateAccount(request).ConfigureAwait(false);
        return Ok(response);
    }
}
