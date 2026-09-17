using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using UsenetSharp.Exceptions;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public sealed class MultiProviderNntpClientConclusiveWalkTests
{
    [Fact]
    public async Task Stat_TransportThen430_ReturnsMissingByDefault()
    {
        var flaky = new MultiProviderNntpClientTests.ScriptedNntpClient
        {
            BatchResponseCode = 430,
            SingularException = _ => new IOException("connection reset"),
        };
        var missing = new MultiProviderNntpClientTests.ScriptedNntpClient
        {
            BatchResponseCode = 430,
            StatResponseCode = 430,
        };
        using var client = new MultiProviderNntpClient(
        [
            MultiProviderNntpClientTests.CreateProvider(flaky, host: "a.example"),
            MultiProviderNntpClientTests.CreateProvider(missing, host: "b.example"),
        ]);

        var response = await client.StatAsync("seg@conclusive", CancellationToken.None);

        Assert.True(UsenetArticleAvailability.IsDefinitiveMissing(response));
    }

    [Fact]
    public async Task Stat_TransportThen430_UnderConclusiveScope_RethrowsTransportFailure()
    {
        var flaky = new MultiProviderNntpClientTests.ScriptedNntpClient
        {
            BatchResponseCode = 430,
            SingularException = _ => new IOException("connection reset"),
        };
        var missing = new MultiProviderNntpClientTests.ScriptedNntpClient
        {
            BatchResponseCode = 430,
            StatResponseCode = 430,
        };
        using var client = new MultiProviderNntpClient(
        [
            MultiProviderNntpClientTests.CreateProvider(flaky, host: "a.example"),
            MultiProviderNntpClientTests.CreateProvider(missing, host: "b.example"),
        ]);

        using (ConclusiveAvailabilityContext.Begin())
        {
            var exception = await Assert.ThrowsAsync<IOException>(
                () => client.StatAsync("seg@conclusive", CancellationToken.None));
            Assert.Equal("connection reset", exception.Message);
        }

        Assert.True(flaky.SingularRequests >= 1);
        Assert.True(missing.SingularRequests >= 1);
    }

    [Fact]
    public async Task Stat_TransportThenThrown430_UnderConclusiveScope_RethrowsTransportFailure()
    {
        var flaky = new MultiProviderNntpClientTests.ScriptedNntpClient
        {
            BatchResponseCode = 430,
            SingularException = _ => new TimeoutException("stat timed out"),
        };
        var missing = new MultiProviderNntpClientTests.ScriptedNntpClient
        {
            BatchResponseCode = 430,
            SingularException = id => new UsenetArticleNotFoundException(id),
        };
        using var client = new MultiProviderNntpClient(
        [
            MultiProviderNntpClientTests.CreateProvider(flaky, host: "a.example"),
            MultiProviderNntpClientTests.CreateProvider(missing, host: "b.example"),
        ]);

        using (ConclusiveAvailabilityContext.Begin())
            await Assert.ThrowsAsync<TimeoutException>(
                () => client.StatAsync("seg@conclusive", CancellationToken.None));
    }

    [Fact]
    public async Task Stat_AuthFailureThen430_UnderConclusiveScope_RethrowsAuthFailure()
    {
        var unauthorized = new MultiProviderNntpClientTests.ScriptedNntpClient
        {
            BatchResponseCode = 430,
            SingularException = _ => new CouldNotLoginToUsenetException("481 auth failed", responseCode: 481),
        };
        var missing = new MultiProviderNntpClientTests.ScriptedNntpClient
        {
            BatchResponseCode = 430,
            StatResponseCode = 430,
        };
        using var client = new MultiProviderNntpClient(
        [
            MultiProviderNntpClientTests.CreateProvider(unauthorized, host: "a.example"),
            MultiProviderNntpClientTests.CreateProvider(missing, host: "b.example"),
        ]);

        using (ConclusiveAvailabilityContext.Begin())
            await Assert.ThrowsAsync<CouldNotLoginToUsenetException>(
                () => client.StatAsync("seg@conclusive", CancellationToken.None));
    }

    [Fact]
    public async Task Stat_All430_UnderConclusiveScope_StillReturnsMissing()
    {
        using var client = new MultiProviderNntpClient(
        [
            MultiProviderNntpClientTests.CreateProvider(
                new MultiProviderNntpClientTests.ScriptedNntpClient
                {
                    BatchResponseCode = 430,
                    StatResponseCode = 430,
                }, host: "a.example"),
            MultiProviderNntpClientTests.CreateProvider(
                new MultiProviderNntpClientTests.ScriptedNntpClient
                {
                    BatchResponseCode = 430,
                    StatResponseCode = 430,
                }, host: "b.example"),
        ]);

        using (ConclusiveAvailabilityContext.Begin())
        {
            var response = await client.StatAsync("seg@conclusive", CancellationToken.None);
            Assert.True(UsenetArticleAvailability.IsDefinitiveMissing(response));
        }
    }

    [Fact]
    public async Task Stat_430ThenTransport_UnderConclusiveScope_StillThrowsTransport()
    {
        var missing = new MultiProviderNntpClientTests.ScriptedNntpClient
        {
            BatchResponseCode = 430,
            StatResponseCode = 430,
        };
        var flaky = new MultiProviderNntpClientTests.ScriptedNntpClient
        {
            BatchResponseCode = 430,
            SingularException = _ => new IOException("connection reset"),
        };
        using var client = new MultiProviderNntpClient(
        [
            MultiProviderNntpClientTests.CreateProvider(missing, host: "a.example"),
            MultiProviderNntpClientTests.CreateProvider(flaky, host: "b.example"),
        ]);

        using (ConclusiveAvailabilityContext.Begin())
            await Assert.ThrowsAsync<IOException>(
                () => client.StatAsync("seg@conclusive", CancellationToken.None));
    }

    [Fact]
    public async Task Stat_StorageGroupSiblingSkip_UnderConclusiveScope_StillReturnsMissing()
    {
        var first = new MultiProviderNntpClientTests.ScriptedNntpClient
        {
            BatchResponseCode = 430,
            StatResponseCode = 430,
        };
        var sibling = new MultiProviderNntpClientTests.ScriptedNntpClient
        {
            BatchResponseCode = 430,
            StatResponseCode = 430,
        };
        using var client = new MultiProviderNntpClient(
        [
            MultiProviderNntpClientTests.CreateProvider(first, host: "a.example", storageGroup: "g"),
            MultiProviderNntpClientTests.CreateProvider(sibling, host: "b.example", storageGroup: "g"),
        ]);

        using (ConclusiveAvailabilityContext.Begin())
        {
            var response = await client.StatAsync("seg@conclusive", CancellationToken.None);
            Assert.True(UsenetArticleAvailability.IsDefinitiveMissing(response));
        }

        Assert.Equal(1, first.SingularRequests);
        Assert.Equal(0, sibling.SingularRequests);
    }

    [Fact]
    public void ConclusiveAvailabilityContext_RestoresPreviousValueOnDispose()
    {
        Assert.False(ConclusiveAvailabilityContext.IsActive);
        using (ConclusiveAvailabilityContext.Begin())
        {
            Assert.True(ConclusiveAvailabilityContext.IsActive);
            using (ConclusiveAvailabilityContext.Begin()) Assert.True(ConclusiveAvailabilityContext.IsActive);
            Assert.True(ConclusiveAvailabilityContext.IsActive);
        }
        Assert.False(ConclusiveAvailabilityContext.IsActive);
    }
}