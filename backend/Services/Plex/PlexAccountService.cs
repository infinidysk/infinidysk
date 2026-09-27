using System.Security.Cryptography;

namespace NzbWebDAV.Services.Plex;

/// <summary>Process-local, expiring handles. Tokens never leave server-side entries.</summary>
public sealed class PlexAccountService(PlexApiClient api, TimeProvider clock, int capacity = 128)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LoginEntry> _logins = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ServerEntry> _servers = new(StringComparer.Ordinal);

    public PlexLoginStart OpenConnected(string owner, string token, string? accountId = null)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(token)) throw Denied();
        lock (_gate)
        {
            Prune();
            EnsureCapacity(1);
            var handle = Handle();
            var entry = new LoginEntry(owner, clock.GetUtcNow().AddMinutes(15)) { Token = token, AccountId = accountId };
            _logins.Add(handle, entry);
            return new(handle, "", entry.ExpiresAt);
        }
    }

    public string ResolveToken(string owner, string handle)
    {
        lock (_gate) return RequireLogin(owner, handle).Token
            ?? throw new InvalidOperationException("Complete Plex login before using the account.");
    }

    public void BindAccount(string owner, string handle, string expectedToken, string accountId)
    {
        lock (_gate)
        {
            var entry = RequireLogin(owner, handle);
            if (entry.Token != expectedToken) throw Denied();
            entry.AccountId = accountId;
        }
    }

    public PlexLoginStart OpenDerived(string owner, string parentHandle, string expectedToken, string token, string accountId)
    {
        lock (_gate)
        {
            if (RequireLogin(owner, parentHandle).Token != expectedToken) throw Denied();
            return OpenConnected(owner, token, accountId);
        }
    }

    public void ForgetAccount(string accountId, string? token)
    {
        lock (_gate)
            foreach (var pair in _logins.Where(pair => pair.Value.AccountId == accountId ||
                         (token is not null && pair.Value.Token == token)).ToArray())
                Cancel(pair.Value.Owner, pair.Key);
    }

    public async Task<PlexLoginStart> StartAsync(string owner, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(owner)) throw Denied();
        var handle = Handle();
        var entry = new LoginEntry(owner, clock.GetUtcNow().AddMinutes(15));
        lock (_gate)
        {
            Prune();
            EnsureCapacity(1);
            _logins.Add(handle, entry);
        }
        try
        {
            var pin = await api.StartPinAsync(ct).ConfigureAwait(false);
            lock (_gate)
            {
                entry.Pin = pin.Id;
                entry.ExpiresAt = clock.GetUtcNow().AddSeconds(Math.Clamp(pin.ExpiresIn, 1, 900));
            }
            var url = $"https://app.plex.tv/auth#?clientID={Uri.EscapeDataString(api.InstallationId)}&code={Uri.EscapeDataString(pin.Code)}&context[device][product]=InfiniDysk";
            return new(handle, url, entry.ExpiresAt);
        }
        catch
        {
            lock (_gate) _logins.Remove(handle);
            throw;
        }
    }

    public async Task<PlexLoginStatus> PollAsync(string owner, string handle, CancellationToken ct = default)
    {
        LoginEntry entry;
        lock (_gate)
        {
            if (!_logins.TryGetValue(handle, out entry!)) return new("expired", null);
            RequireOwner(owner, entry.Owner);
        }
        await entry.PollGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (entry.Cancelled) return new("cancelled", entry.ExpiresAt);
                if (entry.ExpiresAt <= clock.GetUtcNow()) { entry.Token = null; return new("expired", entry.ExpiresAt); }
                if (entry.Token is not null) return new("connected", entry.ExpiresAt);
                if (entry.Pin == 0) return new("pending", entry.ExpiresAt);
            }
            var pin = await api.PollPinAsync(entry.Pin, ct).ConfigureAwait(false);
            lock (_gate)
            {
                if (entry.Cancelled) return new("cancelled", entry.ExpiresAt);
                if (entry.ExpiresAt <= clock.GetUtcNow()) return new("expired", entry.ExpiresAt);
                entry.Token = string.IsNullOrWhiteSpace(pin.AuthToken) ? null : pin.AuthToken;
                return new(entry.Token is null ? "pending" : "connected", entry.ExpiresAt);
            }
        }
        finally { entry.PollGate.Release(); }
    }

    public async Task<IReadOnlyList<PlexServerHandle>> DiscoverAsync(string owner, string handle, CancellationToken ct = default)
    {
        LoginEntry entry;
        string token;
        lock (_gate)
        {
            entry = RequireLogin(owner, handle);
            token = entry.Token ?? throw new InvalidOperationException("Complete Plex login before discovering servers.");
        }
        var discovered = await api.DiscoverAsync(token, ct).ConfigureAwait(false);
        lock (_gate)
        {
            _ = RequireLogin(owner, handle);
            var distinct = discovered.DistinctBy(server => server.Id).ToArray();
            var existing = _servers.Where(pair => pair.Value.LoginHandle == handle).ToDictionary(pair => pair.Value.Server.Id, pair => pair.Key);
            EnsureCapacity(distinct.Count(server => !existing.ContainsKey(server.Id)));
            var result = new List<PlexServerHandle>();
            foreach (var server in distinct)
            {
                var serverHandle = existing.GetValueOrDefault(server.Id) ?? Handle();
                _servers[serverHandle] = new(owner, handle, server, entry.ExpiresAt);
                result.Add(new(serverHandle, server.Id, server.Name, server.Connections));
            }
            return result;
        }
    }

    public PlexDiscoveredServer ResolveServer(string owner, string handle)
    {
        lock (_gate)
        {
            if (!_servers.TryGetValue(handle, out var entry) || entry.ExpiresAt <= clock.GetUtcNow()) throw Denied();
            RequireOwner(owner, entry.Owner);
            var login = RequireLogin(owner, entry.LoginHandle);
            return entry.Server with { AccountId = login.AccountId };
        }
    }

    public void Cancel(string owner, string handle)
    {
        lock (_gate)
        {
            if (!_logins.TryGetValue(handle, out var entry)) return;
            RequireOwner(owner, entry.Owner);
            entry.Cancelled = true;
            entry.Token = null;
            foreach (var key in _servers.Where(pair => pair.Value.LoginHandle == handle).Select(pair => pair.Key).ToArray())
                _servers.Remove(key);
        }
    }

    private LoginEntry RequireLogin(string owner, string handle)
    {
        if (!_logins.TryGetValue(handle, out var entry)) throw Denied();
        RequireOwner(owner, entry.Owner);
        if (entry.Cancelled) throw new InvalidOperationException("Plex login was cancelled.");
        if (entry.ExpiresAt <= clock.GetUtcNow()) throw Denied();
        return entry;
    }
    private void Prune()
    {
        var now = clock.GetUtcNow();
        foreach (var key in _servers.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray()) _servers.Remove(key);
        foreach (var key in _logins.Where(pair => pair.Value.ExpiresAt <= now || pair.Value.Cancelled).Select(pair => pair.Key).ToArray()) _logins.Remove(key);
    }
    private void EnsureCapacity(int count)
    {
        if (_logins.Count + _servers.Count + count > capacity)
            throw new InvalidOperationException("Plex login capacity is reached; cancel an existing login or retry after expiry.");
    }
    private static string Handle() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    private static void RequireOwner(string actual, string expected) { if (actual != expected) throw Denied(); }
    private static UnauthorizedAccessException Denied() => new("The Plex handle is unavailable for this administrator session.");
    private sealed class LoginEntry(string owner, DateTimeOffset expiresAt)
    {
        public string Owner { get; } = owner;
        public DateTimeOffset ExpiresAt { get; set; } = expiresAt;
        public int Pin { get; set; }
        public string? Token { get; set; }
        public string? AccountId { get; set; }
        public bool Cancelled { get; set; }
        public SemaphoreSlim PollGate { get; } = new(1, 1);
    }
    private sealed record ServerEntry(string Owner, string LoginHandle, PlexDiscoveredServer Server, DateTimeOffset ExpiresAt);
}
