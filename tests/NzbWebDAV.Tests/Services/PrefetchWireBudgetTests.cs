using NzbWebDAV.Services.Prefetch;
using NzbWebDAV.Clients.Usenet;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Data.Sqlite;

namespace NzbWebDAV.Tests.Services;

public sealed class PrefetchWireBudgetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "prefetch-wire-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SharedAccountingFailure_StopsOtherJobsWithRemainingCredit()
    {
        using var store = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        await using var budget = new PrefetchWireBudget(store, () => 1000, CancellationToken.None);
        budget.Observe(10);
        await budget.SettleAsync(CancellationToken.None);
        store.BlockWireBudget();
        budget.Observe(1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => budget.SettleAsync(budget.Token).AsTask());
        Assert.True(budget.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task EmptyDecodedBatch_SettlesWireHeadersBeforeCompletingAllBodies()
    {
        using var store = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        const string wire = "=ybegin line=128 size=0 name=empty.bin\r\n=yend size=0\r\n.\r\n";
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
            await using var writer = new StreamWriter(stream, Encoding.ASCII, 1024, true) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("200 test server");
            try
            {
                while (await reader.ReadLineAsync(timeout.Token) is { } command)
                {
                    if (command.StartsWith("BODY ", StringComparison.Ordinal))
                    { await writer.WriteLineAsync("222 body follows"); await writer.WriteAsync(wire); }
                    else if (command == "QUIT") { await writer.WriteLineAsync("205 goodbye"); break; }
                }
            }
            catch (IOException) { /* Budget exhaustion intentionally closes the connection. */ }
        }, timeout.Token);
        using (var client = new BaseNntpClient(false, applyBandwidthLimit: false))
        {
            await client.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, false, timeout.Token);
            await using var budget = new PrefetchWireBudget(store, () => 10, timeout.Token);
            using var ambient = budget.Enter();
            var completions = 0;
            var batch = await client.DecodedBodiesAsync([new("one@example.com"), new("two@example.com")],
                (_, _) => Interlocked.Increment(ref completions), budget.Token);
            foreach (var response in batch.Responses)
                try
                {
                    if ((await response).Stream is { } body)
                    {
                        await using (body) await body.CopyToAsync(Stream.Null, timeout.Token);
                    }
                }
                catch (Exception exception) when (exception is not OutOfMemoryException) { }
            try { await batch.Completion.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (OperationCanceledException) { }
            Assert.True(budget.Exceeded);
            Assert.Equal(1, completions);
            foreach (var response in batch.Responses)
                try { if ((await response).Stream is { } body) await body.DisposeAsync(); }
                catch (Exception exception) when (exception is not OutOfMemoryException) { }
        }
        await server.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task ExhaustedTransport_AbandonsInsteadOfWaitingToDrainAndReleasesOnce()
    {
        using var store = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
            await using var writer = new StreamWriter(stream, Encoding.ASCII, 1024, true) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("200 test server");
            Assert.StartsWith("BODY ", await reader.ReadLineAsync(timeout.Token));
            await writer.WriteLineAsync("222 body follows");
            await writer.WriteLineAsync("=ybegin line=128 size=131072 name=test.bin");
            await writer.WriteAsync(string.Concat(Enumerable.Repeat(new string('*', 128) + "\r\n", 1024)));
            // No terminator: a drain-to-reuse implementation would wait for its IO timeout.
            await reader.ReadLineAsync(timeout.Token);
        }, timeout.Token);
        using (var client = new BaseNntpClient(false, TimeSpan.FromSeconds(5), applyBandwidthLimit: false))
        {
            await client.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, false, timeout.Token);
            await using var budget = new PrefetchWireBudget(store, () => 1000, timeout.Token);
            using var ambient = budget.Enter();
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completions = 0;
            var response = await client.DecodedBodyAsync("test@example.com", (_, _) =>
            { Interlocked.Increment(ref completions); completed.TrySetResult(); }, budget.Token);
            await using var body = response.Stream!;
            await Assert.ThrowsAnyAsync<Exception>(() => body.CopyToAsync(Stream.Null, timeout.Token));
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(budget.Exceeded);
            Assert.Equal(1, completions);
            Assert.True(budget.ReceivedBytes > 1000);
        }
        await server.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task ExhaustedBudget_RejectsMissBeforeOpeningSource()
    {
        using var store = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        Assert.True(store.TrySpendDailyBudget(100, 100));
        await using var budget = new PrefetchWireBudget(store, () => 100, CancellationToken.None);
        Assert.False(await budget.PrepareReadAsync(CancellationToken.None));
        Assert.True(budget.Exceeded);
    }

    [Fact]
    public async Task LoweredLimit_RevalidatesOutstandingCredit()
    {
        using var store = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        long limit = 100;
        await using var budget = new PrefetchWireBudget(store, () => limit, CancellationToken.None);
        budget.Observe(10);
        await budget.SettleAsync(CancellationToken.None);
        limit = 20;
        budget.Observe(15);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => budget.SettleAsync(budget.Token).AsTask());
        Assert.True(budget.Exceeded);
    }

    [Fact]
    public async Task SettlementFailure_CancelsBudgetRatherThanLosingDebtAndContinuing()
    {
        var path = Path.Combine(_root, "jobs.db");
        using var store = new PrefetchJobStore(path);
        await using var budget = new PrefetchWireBudget(store, () => 100, CancellationToken.None);
        using var database = new SqliteConnection("Data Source=" + path);
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = "CREATE TRIGGER RejectCredit BEFORE INSERT ON DailyBudget BEGIN SELECT RAISE(ABORT,'credit unavailable'); END;";
        command.ExecuteNonQuery();
        budget.Observe(10);
        await Assert.ThrowsAnyAsync<Exception>(() => budget.SettleAsync(budget.Token).AsTask());
        Assert.True(budget.Token.IsCancellationRequested);
        Assert.True(budget.Exceeded);
    }

    [Fact]
    public async Task ReusedNntpClient_AttributesRawPayloadToEachJobAndCompletesOnce()
    {
        using var store = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        const string wire = "=ybegin line=128 size=3 name=test.bin\r\n+,-\r\n=yend size=3\r\n.\r\n";
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
            await using var writer = new StreamWriter(stream, Encoding.ASCII, 1024, true) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("200 test server");
            while (await reader.ReadLineAsync(timeout.Token) is { } command)
            {
                if (command.StartsWith("BODY ", StringComparison.Ordinal))
                { await writer.WriteLineAsync("222 body follows"); await writer.WriteAsync(wire); }
                else if (command == "QUIT") { await writer.WriteLineAsync("205 goodbye"); break; }
                else await writer.WriteLineAsync("500 unsupported");
            }
        }, timeout.Token);
        long providerBytes = 0;
        using (var client = new BaseNntpClient(false, applyBandwidthLimit: false,
            payloadBytesObserver: bytes => Interlocked.Add(ref providerBytes, bytes)))
        {
            await client.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, false, timeout.Token);
            for (var index = 0; index < 2; index++)
            {
                await using var budget = new PrefetchWireBudget(store, () => 10000, timeout.Token);
                using var ambient = budget.Enter();
                var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var completions = 0;
                var response = await client.DecodedBodyAsync("test@example.com", (_, _) =>
                { Interlocked.Increment(ref completions); completed.TrySetResult(); }, budget.Token);
                await using var body = response.Stream!;
                await body.CopyToAsync(Stream.Null, timeout.Token);
                await completed.Task.WaitAsync(timeout.Token);
                Assert.Equal(1, completions);
                Assert.Equal(wire.Length - 3, budget.ReceivedBytes);
            }
        }
        await server.WaitAsync(timeout.Token);
        Assert.Equal(2 * (wire.Length - 3), providerBytes);
    }

    [Fact]
    public async Task CacheHitCostsNothing_AndUnusedWireCreditsAreReturned()
    {
        using var store = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        await using (var hit = new PrefetchWireBudget(store, () => 100, CancellationToken.None)) { }
        await using (var budget = new PrefetchWireBudget(store, () => 100, CancellationToken.None))
        {
            budget.Observe(60);
            await budget.SettleAsync(CancellationToken.None);
            Assert.Equal(60, budget.ReceivedBytes);
        }
        Assert.True(store.TrySpendDailyBudget(40, 100));
        Assert.False(store.TrySpendDailyBudget(1, 100));
    }

    [Fact]
    public async Task ExhaustionCancelsAndAccountsReceivedFailurePayload()
    {
        using var store = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        await using var budget = new PrefetchWireBudget(store, () => 50, CancellationToken.None);
        budget.Observe(60);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => budget.SettleAsync(budget.Token).AsTask());
        Assert.True(budget.Exceeded);
        Assert.True(budget.Token.IsCancellationRequested);
        Assert.False(store.TrySpendDailyBudget(1, 60));
    }

    [Fact]
    public async Task AmbientBudgetFlowsToBackgroundWorkButNotNextPooledOperation()
    {
        using var store = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        await using var budget = new PrefetchWireBudget(store, () => 100, CancellationToken.None);
        using (budget.Enter())
            await Task.Run(() => { Assert.Same(budget, PrefetchWireBudget.Current); PrefetchWireBudget.Current!.Observe(12); });
        Assert.Null(PrefetchWireBudget.Current);
        Assert.Equal(12, budget.ReceivedBytes);
    }

    [Fact]
    public async Task DisposalWaitsForTransportCompletionBeforeRefundingCredits()
    {
        using var store = new PrefetchJobStore(Path.Combine(_root, "jobs.db"));
        var budget = new PrefetchWireBudget(store, () => 100, CancellationToken.None);
        var operation = budget.BeginOperation();
        budget.Observe(10);
        await budget.SettleAsync(CancellationToken.None);
        var disposal = budget.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        budget.Observe(20);
        operation.Dispose();
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(store.TrySpendDailyBudget(70, 100));
        Assert.False(store.TrySpendDailyBudget(1, 100));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
