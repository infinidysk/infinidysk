using System.Text.Json;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Services.Plex;

public sealed record PlexServerSaveRequest
{
    public string? Handle { get; init; }
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public string? Token { get; init; }
    public bool Enabled { get; init; } = true;
    public IReadOnlyList<PlexPathMapping> PathMappings { get; init; } = [];
    public override string ToString() => "PlexServerSaveRequest { credentials = [redacted] }";
}

public sealed class PlexServerConfigService(ConfigManager config, ConfigUpdateService updates, DavDatabaseClient db,
    PlexAccountService accounts, PlexApiClient api)
{
    public IReadOnlyList<PlexServer> GetServers()
    {
        var json = config.GetEffectiveConfigValue(PlexSettings.ServersKey) ?? "[]";
        return PlexSettings.ParseServers(Masker().MaskForResponse(PlexSettings.ServersKey, json));
    }

    public PlexServer GetServer(string id) => PlexSettings.ParseServers(config.GetEffectiveConfigValue(PlexSettings.ServersKey))
        .SingleOrDefault(server => server.Id == id) ?? throw new ArgumentException("The selected Plex server is not configured.");

    public Task<PlexIdentity> TestAsync(string owner, PlexServerSaveRequest request, CancellationToken ct = default) =>
        api.TestServerAsync(Resolve(owner, request), ct);

    public async Task<IReadOnlyList<PlexServer>> SaveAsync(string owner, IReadOnlyList<PlexServerSaveRequest> requests, CancellationToken ct = default)
    {
        if (requests.Count > 32) throw new ArgumentException("At most 32 Plex servers can be configured.");
        var servers = new List<PlexServer>();
        var existing = PlexSettings.ParseServers(config.GetEffectiveConfigValue(PlexSettings.ServersKey));
        foreach (var request in requests)
        {
            var candidate = Resolve(owner, request);
            var unchangedConnection = existing.Any(server => server.Id == candidate.Id && server.Token == candidate.Token &&
                PlexSettings.ValidateServerUri(server.Url).ToString().TrimEnd('/') == candidate.Url);
            if (unchangedConnection)
            {
                // Retiring an offline server or changing labels/mappings must not need a live server.
                servers.Add(candidate);
                continue;
            }
            var identity = await api.TestServerAsync(candidate, ct).ConfigureAwait(false);
            servers.Add(candidate with { Id = identity.MachineIdentifier });
        }
        var json = JsonSerializer.Serialize(servers);
        _ = PlexSettings.ParseServers(json);
        using var batch = await updates.StageAsync(() =>
        {
            // Network validation may have waited behind another settings writer. Never revive
            // credentials cancelled or disconnected while this request was waiting for the lease.
            for (var index = 0; index < requests.Count; index++)
            {
                if (requests[index].Token is { } masked && ConfigSecretMasker.IsMaskToken(masked))
                {
                    var currentServer = PlexSettings.ParseServers(config.GetEffectiveConfigValue(PlexSettings.ServersKey))
                        .SingleOrDefault(server => server.Id == servers[index].Id);
                    if (currentServer is null || currentServer.Token != servers[index].Token)
                        throw new InvalidOperationException("Plex credentials changed; reload the server settings before saving.");
                }
                if (string.IsNullOrEmpty(requests[index].Handle)) continue;
                var current = accounts.ResolveServer(owner, requests[index].Handle!);
                if (current.Id != servers[index].Id || current.Token != servers[index].Token ||
                    current.AccountId != servers[index].AccountId)
                    throw new UnauthorizedAccessException("The Plex discovery handle changed.");
            }
            var accountIds = PlexSettings.ParseAccounts(config.GetEffectiveConfigValue(PlexSettings.AccountsKey))
                .Select(account => account.Id).ToHashSet(StringComparer.Ordinal);
            if (servers.Any(server => server.Enabled && server.AccountId is not null && !accountIds.Contains(server.AccountId)))
                throw new InvalidOperationException("Reconnect the Plex account before enabling its server.");
            return [new ConfigItem { ConfigName = PlexSettings.ServersKey, ConfigValue = json }];
        }, ct).ConfigureAwait(false);
        await db.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        updates.Publish(batch);
        return GetServers();
    }

    public async Task DisconnectAsync(string serverId, CancellationToken ct = default)
    {
        using var batch = await updates.StageAsync(() =>
        {
            var retained = PlexSettings.ParseServers(config.GetEffectiveConfigValue(PlexSettings.ServersKey))
                .Where(server => server.Id != serverId).ToArray();
            return [new ConfigItem { ConfigName = PlexSettings.ServersKey, ConfigValue = JsonSerializer.Serialize(retained) }];
        }, ct).ConfigureAwait(false);
        await db.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        updates.Publish(batch);
    }

    private PlexServer Resolve(string owner, PlexServerSaveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var endpoint = PlexSettings.ValidateServerUri(request.Url).ToString().TrimEnd('/');
        var id = request.Id;
        string? accountId = null;
        string? token;
        if (!string.IsNullOrEmpty(request.Handle))
        {
            var discovered = accounts.ResolveServer(owner, request.Handle);
            if (!discovered.Connections.Any(connection => connection.Uri == endpoint))
                throw new ArgumentException("Choose an advertised connection for this Plex server.");
            if (!string.IsNullOrEmpty(id) && id != discovered.Id) throw new ArgumentException("Plex server identity does not match the discovery handle.");
            id = discovered.Id;
            token = discovered.Token;
            accountId = discovered.AccountId;
        }
        else
        {
            token = request.Token;
            if (token is not null && ConfigSecretMasker.IsMaskToken(token))
            {
                var existing = GetServer(id);
                token = Masker().ResolveMaskedJsonSecret(PlexSettings.ServersKey, token, JsonSerializer.Serialize(new[] { existing }));
            }
            var unchanged = PlexSettings.ParseServers(config.GetEffectiveConfigValue(PlexSettings.ServersKey))
                .SingleOrDefault(server => server.Id == id && server.Token == token &&
                    PlexSettings.ValidateServerUri(server.Url).ToString().TrimEnd('/') == endpoint);
            accountId = unchanged?.AccountId;
        }
        if (string.IsNullOrWhiteSpace(token) || token.Length > 8192 || token.Any(char.IsControl))
            throw new ArgumentException("A valid Plex server token is required.");
        return new PlexServer { Id = id, Name = request.Name, Url = endpoint, Token = token,
            Enabled = request.Enabled, PathMappings = request.PathMappings, AccountId = accountId };
    }

    private static ConfigSecretMasker Masker() => new(EnvironmentUtil.GetRequiredVariable("FRONTEND_BACKEND_API_KEY"));
}
