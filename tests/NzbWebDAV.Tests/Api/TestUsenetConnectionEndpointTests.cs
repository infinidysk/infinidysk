using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NzbWebDAV.Tests.TestUtils;

namespace NzbWebDAV.Tests.Api;

[Collection(nameof(HttpIntegrationCollection))]
public sealed class TestUsenetConnectionEndpointTests
{
    [Fact]
    public async Task SuccessfulTest_DisposesPhysicalConnection()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var connectionClosed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var serverTask = Task.Run(async () =>
        {
            using var tcpClient = await listener.AcceptTcpClientAsync();
            await using var stream = tcpClient.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            await using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\r\n",
            };

            await writer.WriteLineAsync("200 test server ready");
            Assert.StartsWith("AUTHINFO USER ", await reader.ReadLineAsync());
            await writer.WriteLineAsync("381 password required");
            Assert.StartsWith("AUTHINFO PASS ", await reader.ReadLineAsync());
            await writer.WriteLineAsync("281 authentication accepted");

            Assert.Equal("QUIT", await reader.ReadLineAsync());
            await writer.WriteLineAsync("205 closing connection");
            Assert.Null(await reader.ReadLineAsync());
            connectionClosed.TrySetResult();
        });

        await using var factory = new NzbDavWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["host"] = IPAddress.Loopback.ToString(),
            ["port"] = port.ToString(),
            ["use-ssl"] = "false",
            ["skip-tls-verification"] = "false",
            ["user"] = "test-user",
            ["pass"] = "test-pass",
        });
        using var response = await client.PostAsync("/api/test-usenet-connection", form);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("connected").GetBoolean());

        await connectionClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UnresponsiveQuit_StillReachesEofWithinDisposeBudget()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var connectionClosed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var serverTask = Task.Run(async () =>
        {
            using var tcpClient = await listener.AcceptTcpClientAsync();
            await using var stream = tcpClient.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            await using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\r\n",
            };

            await writer.WriteLineAsync("200 test server ready");
            Assert.StartsWith("AUTHINFO USER ", await reader.ReadLineAsync());
            await writer.WriteLineAsync("381 password required");
            Assert.StartsWith("AUTHINFO PASS ", await reader.ReadLineAsync());
            await writer.WriteLineAsync("281 authentication accepted");

            Assert.Equal("QUIT", await reader.ReadLineAsync());
            Assert.Null(await reader.ReadLineAsync());
            connectionClosed.TrySetResult();
        });

        await using var factory = new NzbDavWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["host"] = IPAddress.Loopback.ToString(),
            ["port"] = port.ToString(),
            ["use-ssl"] = "false",
            ["skip-tls-verification"] = "false",
            ["user"] = "test-user",
            ["pass"] = "test-pass",
        });
        using var response = await client.PostAsync("/api/test-usenet-connection", form);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("connected").GetBoolean());

        await connectionClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
