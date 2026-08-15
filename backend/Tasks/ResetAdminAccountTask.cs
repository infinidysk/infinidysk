using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Tasks;

/// <summary>
/// One-shot operator escape hatch: when <c>RESET_ADMIN_PASSWORD</c> is set, deletes the
/// admin <c>Accounts</c> row on startup so the UI returns to onboarding. Queue, history,
/// settings, and WebDAV credentials are untouched.
/// </summary>
public static class ResetAdminAccountTask
{
    public const string EnvVarName = "RESET_ADMIN_PASSWORD";

    private const string RemoveEnvVarWarning =
        "Remove it from your environment before the next restart to avoid repeated admin account resets.";

    public static bool IsRequested()
    {
        var value = EnvironmentUtil.GetEnvironmentVariable(EnvVarName)?.ToLowerInvariant();
        return value is "true" or "1" or "yes";
    }

    public static async Task RunIfRequestedAsync(
        DavDatabaseContext context,
        CancellationToken cancellationToken = default)
    {
        if (!IsRequested())
            return;

        var adminAccounts = await context.Accounts
            .Where(a => a.Type == Account.AccountType.Admin)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (adminAccounts.Count > 0)
        {
            context.Accounts.RemoveRange(adminAccounts);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            Log.Warning(
                "Admin account deleted via {EnvVar}. {RemoveWarning}",
                EnvVarName,
                RemoveEnvVarWarning);
        }
        else
        {
            Log.Warning(
                "{EnvVar} is set but no admin account exists. {RemoveWarning}",
                EnvVarName,
                RemoveEnvVarWarning);
        }
    }
}
