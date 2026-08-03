using NzbWebDAV.Config;
using NzbWebDAV.Models;

namespace NzbWebDAV.Tests.Config;

public class UsenetProviderIdentityTests
{
    [Fact]
    public void DeriveProviderId_MatchesAPinnedValue()
    {
        // Env-only installs depend on this value never moving.
        Assert.Equal(
            Guid.Parse("a71c51df-fed5-85cc-9fe3-7fa3866d6724"),
            UsenetProviderIdentity.DeriveProviderId(MakeProvider("news.example.com", "alice")));
    }

    [Fact]
    public void EnsureProviderIds_AssignsTheSameIdOnEveryStart()
    {
        var first = MakeConfig(MakeProvider("news.example.com", "alice"));
        var second = MakeConfig(MakeProvider("news.example.com", "alice"));

        Assert.True(UsenetProviderIdentity.EnsureProviderIds(first));
        Assert.True(UsenetProviderIdentity.EnsureProviderIds(second));

        Assert.NotEqual(Guid.Empty, first.Providers[0].ProviderId);
        Assert.Equal(first.Providers[0].ProviderId, second.Providers[0].ProviderId);
    }

    [Fact]
    public void EnsureProviderIds_KeepsTwoAccountsOnOneHostApart()
    {
        var config = MakeConfig(
            MakeProvider("news.example.com", "alice"),
            MakeProvider("news.example.com", "bob"));

        UsenetProviderIdentity.EnsureProviderIds(config);

        Assert.Equal(
            UsenetProviderIdentity.DeriveProviderId(MakeProvider("news.example.com", "alice")),
            config.Providers[0].ProviderId);
        Assert.Equal(
            UsenetProviderIdentity.DeriveProviderId(MakeProvider("news.example.com", "bob")),
            config.Providers[1].ProviderId);
    }

    [Fact]
    public void EnsureProviderIds_GivesRepeatedAccountsStableDistinctIds()
    {
        var first = MakeConfig(
            MakeProvider("news.example.com", "alice"),
            MakeProvider("news.example.com", "alice"));
        var second = MakeConfig(
            MakeProvider("news.example.com", "alice"),
            MakeProvider("news.example.com", "alice"));

        UsenetProviderIdentity.EnsureProviderIds(first);
        UsenetProviderIdentity.EnsureProviderIds(second);

        Assert.Equal(
            UsenetProviderIdentity.DeriveProviderId(MakeProvider("news.example.com", "alice")),
            first.Providers[0].ProviderId);
        Assert.NotEqual(first.Providers[0].ProviderId, first.Providers[1].ProviderId);
        Assert.Equal(first.Providers[0].ProviderId, second.Providers[0].ProviderId);
        Assert.Equal(first.Providers[1].ProviderId, second.Providers[1].ProviderId);
    }

    [Fact]
    public void DeriveProviderId_FoldsHostCaseButNotUserCase()
    {
        var lowerHost = MakeProvider("news.example.com", "alice");
        var upperHost = MakeProvider("NEWS.EXAMPLE.COM", "alice");
        var upperUser = MakeProvider("news.example.com", "Alice");
        var otherPort = MakeProvider("news.example.com", "alice", port: 119);

        Assert.Equal(
            UsenetProviderIdentity.DeriveProviderId(lowerHost),
            UsenetProviderIdentity.DeriveProviderId(upperHost));
        Assert.NotEqual(
            UsenetProviderIdentity.DeriveProviderId(lowerHost),
            UsenetProviderIdentity.DeriveProviderId(upperUser));
        Assert.NotEqual(
            UsenetProviderIdentity.DeriveProviderId(lowerHost),
            UsenetProviderIdentity.DeriveProviderId(otherPort));
    }

    [Fact]
    public void DeriveProviderId_IgnoresThePassword()
    {
        var before = MakeProvider("news.example.com", "alice");
        var after = MakeProvider("news.example.com", "alice");
        after.Pass = "rotated";

        Assert.Equal(
            UsenetProviderIdentity.DeriveProviderId(before),
            UsenetProviderIdentity.DeriveProviderId(after));
    }

    [Fact]
    public void NormalizeProviderIdsOnSave_DerivesWhenNothingStoredMatches()
    {
        var incoming = MakeConfig(MakeProvider("news.example.com", "alice"));
        UsenetProviderIdentity.NormalizeProviderIdsOnSave(incoming, existing: null);

        Assert.Equal(
            UsenetProviderIdentity.DeriveProviderId(MakeProvider("news.example.com", "alice")),
            incoming.Providers[0].ProviderId);
    }

    [Fact]
    public void NormalizeProviderIdsOnSave_DoesNotHandOutAnIdItWillLaterRecover()
    {
        // The stored entry holds the id a new entry at its old host would derive.
        var renamed = MakeProvider("old.example.com", "alice");
        renamed.ProviderId = UsenetProviderIdentity.DeriveProviderId(
            MakeProvider("new.example.com", "alice"));

        var incoming = MakeConfig(
            MakeProvider("new.example.com", "alice"),
            MakeProvider("old.example.com", "alice"));
        UsenetProviderIdentity.NormalizeProviderIdsOnSave(incoming, MakeConfig(renamed));

        Assert.Equal(renamed.ProviderId, incoming.Providers[1].ProviderId);
        Assert.NotEqual(incoming.Providers[0].ProviderId, incoming.Providers[1].ProviderId);
    }

    [Fact]
    public void DeriveProviderId_SeparatesAnOccurrenceFromAUsernameThatLooksLikeOne()
    {
        Assert.NotEqual(
            UsenetProviderIdentity.DeriveProviderId(
                MakeProvider("news.example.com", "alice"), occurrence: 1),
            UsenetProviderIdentity.DeriveProviderId(
                MakeProvider("news.example.com", "alice\n1")));
    }

    [Fact]
    public void DeriveProviderId_SurvivesAMissingHost()
    {
        var provider = MakeProvider("news.example.com", "alice");
        provider.Host = null!;

        Assert.NotEqual(Guid.Empty, UsenetProviderIdentity.DeriveProviderId(provider));
    }

    private static UsenetProviderConfig MakeConfig(
        params UsenetProviderConfig.ConnectionDetails[] providers)
    {
        return new UsenetProviderConfig { Providers = [.. providers] };
    }

    private static UsenetProviderConfig.ConnectionDetails MakeProvider(
        string host,
        string user,
        int port = 563
    )
    {
        return new UsenetProviderConfig.ConnectionDetails
        {
            Type = ProviderType.Pooled,
            Host = host,
            Port = port,
            UseSsl = true,
            User = user,
            Pass = "pass",
            MaxConnections = 10,
        };
    }
}
