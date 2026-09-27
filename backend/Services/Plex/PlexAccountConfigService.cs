using System.Text.Json;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Services.Plex;

public sealed class PlexAccountConfigService(ConfigManager config, ConfigUpdateService updates, DavDatabaseClient db,
    PlexAccountService accounts, PlexApiClient api)
{
    public IReadOnlyList<PlexAccount> GetAccounts() => PlexSettings.ParseAccounts(Masker().MaskForResponse(
        PlexSettings.AccountsKey, config.GetEffectiveConfigValue(PlexSettings.AccountsKey) ?? "[]"));

    public async Task<PlexLoginStart> SelectAsync(string owner, string accountId, CancellationToken ct = default)
    {
        // Serialize account lookup and handle creation with disconnect's token invalidation.
        using var lease = await updates.StageAsync([], ct).ConfigureAwait(false);
        var account = StoredAccounts().SingleOrDefault(item => item.Id == accountId)
            ?? throw new ArgumentException("The selected Plex account is not configured.");
        return accounts.OpenConnected(owner, account.Token, account.Id);
    }

    public async Task<PlexAccount> SaveLoginAsync(string owner, string handle, CancellationToken ct = default)
    {
        var token = accounts.ResolveToken(owner, handle);
        var account = await api.GetAccountAsync(token, ct).ConfigureAwait(false);
        if (accounts.ResolveToken(owner, handle) != token) throw new UnauthorizedAccessException("The Plex account handle changed.");
        await SaveAsync(account, () => RequireToken(owner, handle, token),
            () => accounts.BindAccount(owner, handle, token, account.Id), ct).ConfigureAwait(false);
        return GetAccounts().Single(item => item.Id == account.Id);
    }

    public Task<IReadOnlyList<PlexHomeUser>> GetHomeUsersAsync(string owner, string handle, CancellationToken ct = default) =>
        api.GetHomeUsersAsync(accounts.ResolveToken(owner, handle), ct);

    public async Task<PlexHomeSelection> SwitchHomeUserAsync(string owner, string handle, string userId, string? pin, CancellationToken ct = default)
    {
        var original = accounts.ResolveToken(owner, handle);
        var users = await api.GetHomeUsersAsync(original, ct).ConfigureAwait(false);
        if (!users.Any(user => user.Id == userId)) throw new ArgumentException("The selected Home user does not belong to this Plex account.");
        var token = await api.SwitchHomeUserAsync(original, userId, pin, ct).ConfigureAwait(false);
        var account = await api.GetAccountAsync(token, ct).ConfigureAwait(false);
        if (accounts.ResolveToken(owner, handle) != original) throw new UnauthorizedAccessException("The Plex account handle changed.");
        PlexLoginStart? opened = null;
        await SaveAsync(account, () => RequireToken(owner, handle, original),
            () => opened = accounts.OpenDerived(owner, handle, original, token, account.Id), ct).ConfigureAwait(false);
        return new(opened!.Handle, account.Id, opened.ExpiresAt);
    }

    public async Task DisconnectAsync(string accountId, CancellationToken ct = default)
    {
        string? removedToken = null;
        using var batch = await updates.StageAsync(() =>
        {
            var existing = StoredAccounts();
            removedToken = existing.SingleOrDefault(account => account.Id == accountId)?.Token;
            var changes = new List<ConfigItem> { new() { ConfigName = PlexSettings.AccountsKey,
                ConfigValue = JsonSerializer.Serialize(existing.Where(account => account.Id != accountId)) } };
            var servers = PlexSettings.ParseServers(config.GetEffectiveConfigValue(PlexSettings.ServersKey));
            if (servers.Any(server => server.AccountId == accountId && server.Enabled))
                changes.Add(new ConfigItem { ConfigName = PlexSettings.ServersKey, ConfigValue = JsonSerializer.Serialize(
                    servers.Select(server => server.AccountId == accountId ? server with { Enabled = false } : server)) });
            return changes;
        }, ct).ConfigureAwait(false);
        await db.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        accounts.ForgetAccount(accountId, removedToken);
        updates.Publish(batch);
    }

    private async Task SaveAsync(PlexAccount account, Action validateHandle, Action bindHandle, CancellationToken ct)
    {
        using var batch = await updates.StageAsync(() =>
        {
            // A cancelled/disconnected handle may have waited behind another settings writer.
            validateHandle();
            var merged = StoredAccounts().Where(existing => existing.Id != account.Id).Append(account).ToArray();
            var json = JsonSerializer.Serialize(merged);
            _ = PlexSettings.ParseAccounts(json);
            return [new ConfigItem { ConfigName = PlexSettings.AccountsKey, ConfigValue = json }];
        }, ct).ConfigureAwait(false);
        await db.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        // Bind before releasing the write lease, so disconnect sees every handle for this identity.
        // Once SQLite committed, publish even if a concurrent login cancellation rejects binding.
        try { bindHandle(); }
        finally { updates.Publish(batch); }
    }
    private void RequireToken(string owner, string handle, string expected)
    {
        if (accounts.ResolveToken(owner, handle) != expected)
            throw new UnauthorizedAccessException("The Plex account handle changed.");
    }
    private IReadOnlyList<PlexAccount> StoredAccounts() => PlexSettings.ParseAccounts(config.GetEffectiveConfigValue(PlexSettings.AccountsKey));
    private static ConfigSecretMasker Masker() => new(EnvironmentUtil.GetRequiredVariable("FRONTEND_BACKEND_API_KEY"));
}

public sealed record PlexHomeSelection(string Handle, string AccountId, DateTimeOffset ExpiresAt);
