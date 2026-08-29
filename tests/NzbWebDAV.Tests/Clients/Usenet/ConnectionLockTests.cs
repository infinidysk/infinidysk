using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class ConnectionLockTests
{
    [Fact]
    public void Dispose_ReleasesAttachedCallbackOnceAfterConnection()
    {
        var returned = 0;
        var callback = 0;
        var connectionLock = new ConnectionLock<object>(
            new object(),
            _ => Interlocked.Increment(ref returned),
            (_, _) => throw new InvalidOperationException("Unexpected destroy"),
            wasReused: true);
        connectionLock.AttachDisposeCallback(() => Interlocked.Increment(ref callback));

        connectionLock.Dispose();
        connectionLock.Dispose();

        Assert.Equal(1, returned);
        Assert.Equal(1, callback);
    }

    [Fact]
    public void AttachDisposeCallback_RejectsASecondOwner()
    {
        var connectionLock = new ConnectionLock<object>(
            new object(),
            _ => { },
            (_, _) => throw new InvalidOperationException("Unexpected destroy"),
            wasReused: false);
        connectionLock.AttachDisposeCallback(() => { });

        Assert.Throws<InvalidOperationException>(
            () => connectionLock.AttachDisposeCallback(() => { }));
    }

    [Fact]
    public void AttachDisposeCallback_AfterDisposalRunsImmediatelyOnce()
    {
        var callbacks = 0;
        var connectionLock = new ConnectionLock<object>(
            new object(),
            _ => { },
            (_, _) => throw new InvalidOperationException("Unexpected destroy"),
            wasReused: false);
        connectionLock.Dispose();

        connectionLock.AttachDisposeCallback(() => Interlocked.Increment(ref callbacks));
        connectionLock.Dispose();

        Assert.Equal(1, callbacks);
    }

    [Fact]
    public void CompositeOperationLeasesReleaseOnceInReverseAcquisitionOrder()
    {
        var events = new List<string>();
        var leases = new MultiConnectionNntpClient.OperationLeaseGroup();
        leases.Add(new RecordingDisposable(() => events.Add("health")));
        leases.Add(new RecordingDisposable(() => events.Add("provider")));
        var connectionLock = new ConnectionLock<object>(
            new object(),
            _ => events.Add("connection"),
            (_, _) => throw new InvalidOperationException("Unexpected destroy"),
            wasReused: false);
        connectionLock.AttachDisposeCallback(leases.Dispose);

        connectionLock.Dispose();
        connectionLock.Dispose();
        leases.Dispose();

        Assert.Equal(["connection", "provider", "health"], events);
    }

    private sealed class RecordingDisposable(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
