using System.Text.Json;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Services.StreamTrace;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Tests.Config;

public class UsenetProvidersValidationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ValidateConfigItems_RejectsNonPositiveMaxConnections(int maxConnections)
    {
        var items = ProvidersConfigItems(MakeProvider(maxConnections: maxConnections, nickname: "bad-pool"));
        var ex = Assert.Throws<ArgumentException>(() => ConfigManager.ValidateConfigItems(items));
        Assert.Contains("max connections must be at least 1", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bad-pool", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateConfigItems_AcceptsNullMaxTransferConnections()
    {
        var items = ProvidersConfigItems(MakeProvider(maxTransferConnections: null));

        ConfigManager.ValidateConfigItems(items);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ValidateConfigItems_RejectsNonPositiveMaxTransferConnections(int maxTransferConnections)
    {
        var items = ProvidersConfigItems(MakeProvider(
            maxConnections: 10,
            maxTransferConnections: maxTransferConnections,
            nickname: "bad-transfer"));

        var ex = Assert.Throws<ArgumentException>(() => ConfigManager.ValidateConfigItems(items));
        Assert.Contains("transfer connections must be at least 1", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bad-transfer", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateConfigItems_RejectsMaxTransferConnectionsAboveProviderLimit()
    {
        var items = ProvidersConfigItems(MakeProvider(
            maxConnections: 10,
            maxTransferConnections: 11,
            nickname: "oversubscribed"));

        var ex = Assert.Throws<ArgumentException>(() => ConfigManager.ValidateConfigItems(items));
        Assert.Contains("transfer connections must not exceed max connections", ex.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("oversubscribed", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ProviderType.Pooled)]
    [InlineData(ProviderType.BackupAndStats)]
    [InlineData(ProviderType.BackupOnly)]
    [InlineData(ProviderType.Disabled)]
    public void ValidateConfigItems_AcceptsValidMaxTransferConnectionsForEveryProviderType(
        ProviderType providerType)
    {
        var items = ProvidersConfigItems(MakeProvider(
            type: providerType,
            maxConnections: 10,
            maxTransferConnections: 10));

        ConfigManager.ValidateConfigItems(items);
    }

    [Fact]
    public void DeserializeProviderWithoutMaxTransferConnections_UsesLegacyNull()
    {
        const string json = """
                            {
                              "Providers": [
                                {
                                  "Type": 1,
                                  "Host": "legacy.example",
                                  "Port": 563,
                                  "UseSsl": true,
                                  "User": "u",
                                  "Pass": "p",
                                  "MaxConnections": 20
                                }
                              ]
                            }
                            """;

        var config = JsonSerializer.Deserialize<UsenetProviderConfig>(json);

        Assert.NotNull(config);
        Assert.Null(Assert.Single(config.Providers).MaxTransferConnections);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void ValidateConfigItems_RejectsInvalidPort(int port)
    {
        var items = ProvidersConfigItems(MakeProvider(port: port, nickname: "port-bad"));
        var ex = Assert.Throws<ArgumentException>(() => ConfigManager.ValidateConfigItems(items));
        Assert.Contains("port must be between 1 and 65535", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("port-bad", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateConfigItems_RejectsEmptyHost()
    {
        var items = ProvidersConfigItems(MakeProvider(host: "   "));
        var ex = Assert.Throws<ArgumentException>(() => ConfigManager.ValidateConfigItems(items));
        Assert.Contains("host must not be empty", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Provider #1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateConfigItems_RejectsNegativeByteLimit()
    {
        var items = ProvidersConfigItems(MakeProvider(byteLimit: -1, nickname: "block"));
        var ex = Assert.Throws<ArgumentException>(() => ConfigManager.ValidateConfigItems(items));
        Assert.Contains("byte limit must not be negative", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("block", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateConfigItems_RejectsDisabledProviderWithZeroMaxConnections()
    {
        var items = ProvidersConfigItems(MakeProvider(
            type: ProviderType.Disabled,
            maxConnections: 0,
            nickname: "disabled-zero"));
        var ex = Assert.Throws<ArgumentException>(() => ConfigManager.ValidateConfigItems(items));
        Assert.Contains("max connections must be at least 1", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("disabled-zero", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateConfigItems_AcceptsValidMultiProviderPayload()
    {
        var items = ProvidersConfigItems(
            MakeProvider(host: "pool.example", maxConnections: 20, nickname: "pool"),
            MakeProvider(host: "backup.example", type: ProviderType.BackupOnly, maxConnections: 5, nickname: "backup"),
            MakeProvider(host: "off.example", type: ProviderType.Disabled, maxConnections: 1, nickname: "off"));

        ConfigManager.ValidateConfigItems(items);
    }

    [Fact]
    public void UsenetStreamingClient_ClampsLegacyZeroMaxConnectionsWithoutThrowing()
    {
        // Bypass ValidateConfigItems to simulate a pre-existing bad DB row.
        var config = new ConfigManager();
        config.UpdateValues(ProvidersConfigItems(MakeProvider(maxConnections: 0, nickname: "legacy-zero")));

        var client = new UsenetStreamingClient(
            config,
            new WebsocketManager(),
            new ProviderUsageTracker(),
            new MetricsWriter(),
            new ProviderBytesTracker(),
            new StreamTraceBuffer(100),
            new ActiveReadRegistry());

        Assert.NotNull(client);
        client.Dispose();
    }

    private static List<ConfigItem> ProvidersConfigItems(
        params UsenetProviderConfig.ConnectionDetails[] providers)
    {
        return
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetProviders,
                ConfigValue = JsonSerializer.Serialize(new UsenetProviderConfig
                {
                    Providers = [.. providers],
                }),
            },
        ];
    }

    [Fact]
    public void ValidateConfigItems_RejectsUnknownJsonPropertiesOnlyWhenStrict()
    {
        var miscased = new ConfigItem
        {
            ConfigName = ConfigKeys.ArrInstances,
            ConfigValue = "{\"radarrInstances\":[]}",
        };

        // The default UI and API save path is unchanged and tolerates miscased JSON.
        ConfigManager.ValidateConfigItems([miscased]);

        var ex = Assert.Throws<ConfigUnmappedPropertyException>(() =>
            ConfigManager.ValidateConfigItems([miscased], rejectUnknownJsonProperties: true));
        Assert.Equal("$.radarrInstances", ex.JsonPath);
    }

    [Fact]
    public void ValidateConfigItems_StrictStillReportsMalformedJsonAsInvalid()
    {
        var malformed = new ConfigItem
        {
            ConfigName = ConfigKeys.ArrInstances,
            ConfigValue = "{\"RadarrInstances\": [",
        };

        // Malformed input is not a mapping problem, so it keeps the generic error.
        var ex = Assert.Throws<ArgumentException>(() =>
            ConfigManager.ValidateConfigItems([malformed], rejectUnknownJsonProperties: true));
        Assert.IsNotType<ConfigUnmappedPropertyException>(ex);
    }

    private static UsenetProviderConfig.ConnectionDetails MakeProvider(
        string host = "nntp.example",
        int port = 563,
        int maxConnections = 10,
        int? maxTransferConnections = null,
        ProviderType type = ProviderType.Pooled,
        string? nickname = null,
        long? byteLimit = null)
    {
        return new UsenetProviderConfig.ConnectionDetails
        {
            Type = type,
            Host = host,
            Port = port,
            UseSsl = true,
            User = "u",
            Pass = "p",
            MaxConnections = maxConnections,
            MaxTransferConnections = maxTransferConnections,
            Nickname = nickname,
            ByteLimit = byteLimit,
        };
    }
}
