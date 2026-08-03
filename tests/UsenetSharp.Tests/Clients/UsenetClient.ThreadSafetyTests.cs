using UsenetSharp.Clients;

namespace UsenetSharpTest.Clients;

public class ThreadSafetyTests
{
    [Test]
    public async Task ConcurrentCommands_ExecuteSequentially()
    {
        // Arrange
        var client = new UsenetClient();
        var cancellationToken = CancellationToken.None;

        // Connect and authenticate
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);

        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        // Act - Execute multiple commands concurrently
        var tasks = new List<Task<bool>>();

        for (int i = 0; i < 5; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                var dateResult = await client.DateAsync(cancellationToken);
                return dateResult.ResponseCode == 111;
            }));
        }

        var results = await Task.WhenAll(tasks);

        // Assert
        Assert.That(results, Has.All.True, "All concurrent DATE commands should succeed");
    }

    [Test]
    public async Task ConcurrentStatCommands_ExecuteSequentially()
    {
        // Arrange
        var client = new UsenetClient();
        var cancellationToken = CancellationToken.None;
        var segmentIds = new[]
        {
            "8mthBMhpfyOJFM7OPe2RsZhm@CAtZlPkA1OiI.WLo",
            "439e9msqibLI2Ckyt7z6EMUY@iLqyzimBg7ac.t2n",
            "RdmCrBrzTPnTiYj7ze5Gwyt6@mfdz3EInI86g.bfV"
        };

        // Connect and authenticate
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);

        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        // Act - Execute multiple STAT commands concurrently
        var tasks = segmentIds.Select(segmentId => Task.Run(async () =>
        {
            var statResult = await client.StatAsync(segmentId, cancellationToken);
            return statResult.ArticleExists;
        })).ToList();

        var results = await Task.WhenAll(tasks);

        // Assert
        Assert.That(results, Has.All.True, "All concurrent STAT commands should succeed");
    }

    [Test]
    public async Task MixedConcurrentCommands_ExecuteSequentially()
    {
        // Arrange
        var client = new UsenetClient();
        var cancellationToken = CancellationToken.None;

        // Connect and authenticate
        await client.ConnectAsync(Credentials.Host, 563, true, cancellationToken);

        await client.AuthenticateAsync(Credentials.Username, Credentials.Password, cancellationToken);

        // Act - Execute mixed commands concurrently
        var dateTask = Task.Run(async () =>
        {
            var result = await client.DateAsync(cancellationToken);
            return result.ResponseCode == 111;
        });

        var statTask = Task.Run(async () =>
        {
            var result = await client.StatAsync("8mthBMhpfyOJFM7OPe2RsZhm@CAtZlPkA1OiI.WLo", cancellationToken);
            return result.ArticleExists;
        });

        var dateTask2 = Task.Run(async () =>
        {
            var result = await client.DateAsync(cancellationToken);
            return result.ResponseCode == 111;
        });

        var results = await Task.WhenAll(dateTask, statTask, dateTask2);

        // Assert
        Assert.That(results, Has.All.True, "All concurrent mixed commands should succeed");
    }

    [Test]
    public void Dispose_ReleasesResources()
    {
        // Arrange
        var client = new UsenetClient();

        // Act
        client.Dispose();

        // Assert - Should not throw
        Assert.Pass("Client disposed successfully");
    }

    [Test]
    public async Task Dispose_AfterConnection_ReleasesResources()
    {
        // Arrange
        var client = new UsenetClient();
        await client.ConnectAsync(Credentials.Host, 563, true, CancellationToken.None);

        // Act
        client.Dispose();

        // Assert - Should not throw
        Assert.Pass("Client disposed successfully after connection");
    }
}
