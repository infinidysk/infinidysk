using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Logging;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Config;

[Collection(nameof(GlobalLoggerCollection))]
public sealed class ConfigChangeSourceTests : IDisposable
{
    public ConfigChangeSourceTests()
        => SynchronousObserverInvoker.ResetFailureLogThrottleForTests();

    public void Dispose()
        => SynchronousObserverInvoker.ResetFailureLogThrottleForTests();

    [Fact]
    public void Subscribe_DeliversUpdatesUntilDisposed()
    {
        var config = new ConfigManager();
        var hits = 0;
        using (config.Subscribe((_, _) => hits++))
        {
            config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.RcloneHost, ConfigValue = "http://a" }]);
            Assert.Equal(1, hits);
        }

        config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.RcloneHost, ConfigValue = "http://b" }]);
        Assert.Equal(1, hits);
    }

    [Fact]
    public void UpdateValues_ThrowingFirstSubscriber_CommitsAndInvokesLaterSubscriber()
    {
        var config = new ConfigManager();
        var order = new List<string>();
        ConfigManager.ConfigEventArgs? firstArgs = null;
        ConfigManager.ConfigEventArgs? secondArgs = null;
        const string host = "http://observer-test.example";

        config.OnConfigChanged += (_, args) =>
        {
            order.Add("first");
            firstArgs = args;
            throw new InvalidOperationException("config observer");
        };
        config.OnConfigChanged += (_, args) =>
        {
            order.Add("second");
            secondArgs = args;
        };

        config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.RcloneHost, ConfigValue = host }]);

        Assert.Equal(["first", "second"], order);
        Assert.Same(firstArgs, secondArgs);
        Assert.Equal(host, secondArgs!.ChangedConfig[ConfigKeys.RcloneHost]);
        Assert.Equal(host, config.GetRcloneHost());
    }

    [Fact]
    public void UpdateValues_DisposedDuringSnapshot_CurrentDeliveryFinishesThenStops()
    {
        var config = new ConfigManager();
        var firstCount = 0;
        var secondCount = 0;
        IDisposable? secondSub = null;
        using var firstSub = config.Subscribe((_, _) =>
        {
            firstCount++;
            secondSub!.Dispose();
            throw new InvalidOperationException("config observer");
        });
        secondSub = config.Subscribe((_, _) => secondCount++);

        config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.RcloneHost, ConfigValue = "http://one" }]);
        Assert.Equal(1, firstCount);
        Assert.Equal(1, secondCount);

        config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.RcloneHost, ConfigValue = "http://two" }]);
        Assert.Equal(2, firstCount);
        Assert.Equal(1, secondCount);

        firstSub.Dispose();
        secondSub.Dispose();
    }

    [Fact]
    public void UpdateValues_SubscriberAddedDuringDispatch_StartsNextUpdate()
    {
        var config = new ConfigManager();
        var order = new List<string>();
        var added = false;
        void Third(object? _, ConfigManager.ConfigEventArgs __) => order.Add("third");
        config.OnConfigChanged += (_, _) =>
        {
            order.Add("first");
            if (added)
                return;
            added = true;
            config.OnConfigChanged += Third;
        };
        config.OnConfigChanged += (_, _) => order.Add("second");

        config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.RcloneHost, ConfigValue = "http://one" }]);
        Assert.Equal(["first", "second"], order);

        order.Clear();
        config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.RcloneHost, ConfigValue = "http://two" }]);
        Assert.Equal(["first", "second", "third"], order);
    }

    [Fact]
    public void UpdateValues_ReentrantSubscriber_PublishesNestedChangeWithoutDeadlock()
    {
        var config = new ConfigManager();
        var publications = new List<string>();
        var nested = false;
        config.OnConfigChanged += (_, args) =>
        {
            publications.Add("first:" + string.Join(",", args.ChangedConfig.Keys));
            if (nested || !args.ChangedConfig.ContainsKey(ConfigKeys.RcloneHost))
                return;
            nested = true;
            config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.RcloneUser, ConfigValue = "alice" }]);
        };
        config.OnConfigChanged += (_, args) =>
            publications.Add("second:" + string.Join(",", args.ChangedConfig.Keys));

        config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.RcloneHost, ConfigValue = "http://nested" }]);

        Assert.Equal("http://nested", config.GetRcloneHost());
        Assert.Equal("alice", config.GetRcloneUser());
        Assert.Equal(
            [
                "first:rclone.host",
                "first:rclone.user",
                "second:rclone.user",
                "second:rclone.host",
            ],
            publications);
    }
}
