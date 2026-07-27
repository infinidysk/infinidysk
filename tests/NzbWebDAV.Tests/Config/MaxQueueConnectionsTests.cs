using System.Text.Json;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;

namespace NzbWebDAV.Tests.Config;

public class MaxQueueConnectionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Unset_UsesWholePool(string? preset)
    {
        var config = CreateConfig(absolute: null, preset: preset, pooled: 20);
        Assert.Equal(20, config.GetMaxQueueConnections());
    }

    [Theory]
    [InlineData("low", 20, 5)]
    [InlineData("medium", 20, 10)]
    [InlineData("high", 20, 15)]
    [InlineData("max", 20, 20)]
    [InlineData("low", 220, 55)]
    [InlineData("medium", 220, 110)]
    [InlineData("high", 220, 165)]
    [InlineData("max", 220, 220)]
    public void Preset_ScalesWithTheUsersOwnPool(string preset, int pooled, int expected)
    {
        // The point of the preset: one setting means the same share of the
        // budget whatever a user's providers happen to add up to.
        var config = CreateConfig(absolute: null, preset: preset, pooled: pooled);
        Assert.Equal(expected, config.GetMaxQueueConnections());
    }

    [Theory]
    [InlineData("LOW", 20, 5)]
    [InlineData("Medium", 20, 10)]
    public void Preset_IsCaseInsensitive(string preset, int pooled, int expected)
    {
        var config = CreateConfig(absolute: null, preset: preset, pooled: pooled);
        Assert.Equal(expected, config.GetMaxQueueConnections());
    }

    [Fact]
    public void Preset_NeverYieldsZero()
    {
        // A quarter of a two-connection pool rounds to zero; the queue must
        // still be able to make progress.
        var config = CreateConfig(absolute: null, preset: "low", pooled: 2);
        Assert.Equal(1, config.GetMaxQueueConnections());
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("0.5")]
    public void UnrecognisedPreset_FallsBackToWholePool(string preset)
    {
        var config = CreateConfig(absolute: null, preset: preset, pooled: 20);
        Assert.Equal(20, config.GetMaxQueueConnections());
    }

    [Fact]
    public void AbsoluteValue_StillWins()
    {
        // Back-compat: an explicit count keeps its existing meaning even when a
        // preset is also present.
        var config = CreateConfig(absolute: "7", preset: "max", pooled: 20);
        Assert.Equal(7, config.GetMaxQueueConnections());
    }

    [Theory]
    [InlineData("500", 20)]
    [InlineData("0", 1)]
    [InlineData("-3", 1)] // parses, then clamps up to the floor of 1
    public void AbsoluteValue_ClampsToPool(string absolute, int expected)
    {
        var config = CreateConfig(absolute: absolute, preset: null, pooled: 20);
        Assert.Equal(expected, config.GetMaxQueueConnections());
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    public void UnparseableAbsolute_DefersToThePreset(string absolute)
    {
        var config = CreateConfig(absolute: absolute, preset: "medium", pooled: 20);
        Assert.Equal(10, config.GetMaxQueueConnections());
    }

    private static ConfigManager CreateConfig(string? absolute, string? preset, int pooled)
    {
        var providers = JsonSerializer.Serialize(new UsenetProviderConfig
        {
            Providers =
            [
                new UsenetProviderConfig.ConnectionDetails
                {
                    Type = ProviderType.Pooled,
                    Host = "pool.example",
                    Port = 563,
                    UseSsl = true,
                    User = "u",
                    Pass = "p",
                    MaxConnections = pooled,
                },
            ]
        });

        var items = new List<ConfigItem>
        {
            new() { ConfigName = "usenet.providers", ConfigValue = providers },
        };
        if (absolute is not null)
            items.Add(new ConfigItem { ConfigName = "usenet.max-queue-connections", ConfigValue = absolute });
        if (preset is not null)
            items.Add(new ConfigItem { ConfigName = "usenet.max-queue-connections-preset", ConfigValue = preset });

        var config = new ConfigManager();
        config.UpdateValues(items);
        return config;
    }
}
