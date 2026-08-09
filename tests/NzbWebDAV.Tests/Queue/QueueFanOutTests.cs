using System.Text.Json;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Queue;

namespace NzbWebDAV.Tests.Queue;

public class QueueFanOutTests
{
    [Fact]
    public void PrimaryFanOut_MatchesHistoricalSingleItemBudget()
    {
        Assert.Equal(15, QueueFanOut.PrimaryFanOut(10));
        Assert.Equal(50, QueueFanOut.PrimaryFanOut(100));
        Assert.Equal(6, QueueFanOut.PrimaryFanOut(1));
    }

    [Theory]
    [InlineData(10, 1, 9)]
    [InlineData(10, 2, 8)]
    [InlineData(10, 3, 7)]
    [InlineData(1, 1, 1)]
    [InlineData(2, 4, 1)]
    [InlineData(160, 3, 140)]
    public void PrimaryFanOutWhenSharing_LeavesSpareSoftSlots(
        int maxQueue, int secondaryCount, int expected)
    {
        Assert.Equal(expected, QueueFanOut.PrimaryFanOutWhenSharing(maxQueue, secondaryCount));
    }

    [Theory]
    [InlineData(10, 1, 10)]
    [InlineData(10, 2, 5)]
    [InlineData(10, 3, 4)]
    [InlineData(1, 4, 1)]
    public void SecondaryFanOut_DividesBudgetAcrossSecondaries(
        int maxQueue, int secondaryCount, int expected)
    {
        Assert.Equal(expected, QueueFanOut.SecondaryFanOut(maxQueue, secondaryCount));
    }

    [Fact]
    public void GetConcurrency_WithoutContext_UsesPrimaryFanOut()
    {
        var config = CreateConfig(maxQueueConnections: 8);
        Assert.Equal(13, QueueFanOut.GetConcurrency(config, CancellationToken.None));
    }

    [Fact]
    public void GetExactQueueConcurrency_WithoutContext_UsesMaxQueue()
    {
        var config = CreateConfig(maxQueueConnections: 8);
        Assert.Equal(8, QueueFanOut.GetExactQueueConcurrency(config, CancellationToken.None));
    }

    [Fact]
    public void GetExactQueueConcurrency_ClampsSoloPrimaryOvershootToMaxQueue()
    {
        var config = CreateConfig(maxQueueConnections: 10);
        using var cts = new CancellationTokenSource();
        using var ctx = cts.Token.SetContext(new QueueDownloadContext
        {
            IsPrimary = true,
            GetFanOutConcurrency = () => QueueFanOut.PrimaryFanOut(10),
        });

        Assert.Equal(15, QueueFanOut.GetConcurrency(config, cts.Token));
        Assert.Equal(10, QueueFanOut.GetExactQueueConcurrency(config, cts.Token));
    }

    [Fact]
    public void LinkedCancellationToken_PreservesQueueDownloadContextFanOut()
    {
        var config = CreateConfig(maxQueueConnections: 10);
        using var parentCts = new CancellationTokenSource();
        using var parentCtx = parentCts.Token.SetContext(new QueueDownloadContext
        {
            IsPrimary = true,
            GetFanOutConcurrency = () => 7,
        });

        using var linked = ContextualCancellationTokenSource.CreateLinkedTokenSource(parentCts.Token);

        Assert.Equal(7, QueueFanOut.GetConcurrency(config, linked.Token));
        Assert.Equal(7, QueueFanOut.GetExactQueueConcurrency(config, linked.Token));
        Assert.Same(
            parentCts.Token.GetContext<QueueDownloadContext>(),
            linked.Token.GetContext<QueueDownloadContext>());
    }

    [Fact]
    public void QueueDownloadContext_AccumulatesSemaphoreWait()
    {
        var context = new QueueDownloadContext
        {
            IsPrimary = false,
            GetFanOutConcurrency = () => 1,
        };

        context.RecordSemaphoreWait(12);
        context.RecordSemaphoreWait(8);
        context.RecordSemaphoreWait(0);

        Assert.Equal(20, context.SemaphoreWaitMilliseconds);
    }

    private static ConfigManager CreateConfig(int maxQueueConnections)
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetProviders,
                ConfigValue = JsonSerializer.Serialize(new UsenetProviderConfig
                {
                    Providers =
                    [
                        new UsenetProviderConfig.ConnectionDetails
                        {
                            ProviderId = Guid.NewGuid(),
                            Type = NzbWebDAV.Models.ProviderType.Pooled,
                            Host = "nntp.example",
                            Port = 563,
                            UseSsl = true,
                            User = "u",
                            Pass = "p",
                            MaxConnections = 20,
                        },
                    ],
                }),
            },
            new ConfigItem
            {
                ConfigName = ConfigKeys.UsenetMaxQueueConnections,
                ConfigValue = maxQueueConnections.ToString(),
            },
        ]);
        return config;
    }
}

public class QueueWorkerCountConfigTests
{
    [Theory]
    [InlineData(null, 1)]
    [InlineData("", 1)]
    [InlineData("1", 1)]
    [InlineData("4", 4)]
    [InlineData("8", 8)]
    [InlineData("10", 10)]
    [InlineData("0", 1)]
    [InlineData("99", 10)]
    [InlineData("abc", 1)]
    public void GetQueueWorkerCount_ClampsToOneThroughTen(string? configured, int expected)
    {
        var config = new ConfigManager();
        if (configured is not null)
        {
            config.UpdateValues(
            [
                new ConfigItem { ConfigName = ConfigKeys.QueueWorkerCount, ConfigValue = configured },
            ]);
        }

        Assert.Equal(expected, config.GetQueueWorkerCount());
    }
}
