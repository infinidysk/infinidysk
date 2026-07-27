using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Services.StreamTrace;

namespace NzbWebDAV.Tests.Services.StreamTrace;

public class StreamTraceBufferTests
{
    [Fact]
    public void Record_PreservesPerSessionOrderingAndCapsBuffer()
    {
        var buffer = new StreamTraceBuffer(capacity: 100, maxSessions: 50);
        var sessionA = Guid.NewGuid();
        var sessionB = Guid.NewGuid();

        buffer.RangeOpen(sessionA, "/view/a.mkv", "GET", 0, 99, 1000, "ua", "127.0.0.1");
        buffer.Seek(sessionA, 50);
        buffer.Segment(sessionA, "provider-a", SegmentFetch.FetchStatus.Ok, 12, 0, "msgid@a");
        buffer.RangeOpen(sessionB, "/view/b.mkv", "GET", 0, null, 2000, null, null);
        buffer.ZeroFill(sessionA, "missing@a", 64);
        buffer.RangeEnd(sessionA, ReadSession.EndReasonCode.Completed, 100);
        buffer.Failover(sessionB, "p1", "p2", "Missing");

        var eventsA = buffer.GetSessionEvents(sessionA);
        Assert.Equal(5, eventsA.Count);
        Assert.True(eventsA.Zip(eventsA.Skip(1)).All(pair => pair.First.Sequence < pair.Second.Sequence));
        Assert.Equal(StreamTraceKind.RangeOpen.ToString(), eventsA[0].Kind);
        Assert.Equal(StreamTraceKind.RangeEnd.ToString(), eventsA[^1].Kind);

        var sessions = buffer.ListSessions();
        Assert.Contains(sessions, s => s.SessionId == sessionA);
        Assert.Contains(sessions, s => s.SessionId == sessionB);
        Assert.Equal(100, buffer.Capacity);
    }

    [Fact]
    public void RangeEnd_ReportsStallAttributionAndResetsItForTheNextRange()
    {
        var buffer = new StreamTraceBuffer(capacity: 100, maxSessions: 50);
        var session = Guid.NewGuid();

        buffer.RangeOpen(session, "/view/a.mkv", "GET", 0, null, 1000, null, null);
        buffer.AddStall(session, StreamStallKind.ProviderWait, TimeSpan.FromMilliseconds(120));
        buffer.AddStall(session, StreamStallKind.BodyDrain, TimeSpan.FromMilliseconds(30));
        buffer.AddStall(session, StreamStallKind.ConsumerWait, TimeSpan.FromMilliseconds(400));
        // Sub-millisecond writes must still accumulate rather than truncate to zero.
        for (var i = 0; i < 10; i++)
            buffer.AddStall(session, StreamStallKind.ClientWrite, TimeSpan.FromMicroseconds(300));
        buffer.ConnectionAcquired(session, TimeSpan.FromMilliseconds(70), wasReused: true);
        buffer.ConnectionAcquired(session, TimeSpan.FromMilliseconds(500), wasReused: false);
        buffer.RangeEnd(session, ReadSession.EndReasonCode.Aborted, 4096);

        var first = buffer.GetSessionEvents(session).Last();
        Assert.Equal(120, first.ProviderWaitMs);
        Assert.Equal(30, first.BodyDrainMs);
        Assert.Equal(400, first.ConsumerWaitMs);
        Assert.Equal(3, first.ClientWriteMs);
        Assert.Equal(570, first.ConnectionWaitMs);
        Assert.Equal(1, first.ConnectionsReused);
        Assert.Equal(1, first.ConnectionsOpened);

        buffer.RangeOpen(session, "/view/a.mkv", "GET", 4096, null, 1000, null, null);
        buffer.AddStall(session, StreamStallKind.ProviderWait, TimeSpan.FromMilliseconds(15));
        buffer.RangeEnd(session, ReadSession.EndReasonCode.Completed, 8192);

        var second = buffer.GetSessionEvents(session).Last();
        Assert.Equal(15, second.ProviderWaitMs);
        Assert.Null(second.ConsumerWaitMs);
        Assert.Null(second.ConnectionWaitMs);
        Assert.Null(second.ConnectionsOpened);
    }

    [Fact]
    public void AddStall_BeforeAnyRangeOpen_IsIgnored()
    {
        var buffer = new StreamTraceBuffer(capacity: 100, maxSessions: 50);
        var session = Guid.NewGuid();

        // No range has opened, so there is no session to attribute to. This must not
        // create one — otherwise background work would grow the session index forever.
        buffer.AddStall(session, StreamStallKind.ClientWrite, TimeSpan.FromSeconds(1));
        buffer.ConnectionAcquired(session, TimeSpan.FromSeconds(1), wasReused: false);

        Assert.Empty(buffer.ListSessions());
        Assert.Empty(buffer.GetSessionEvents(session));
    }

    [Fact]
    public void DisabledBuffer_RecordsNothing()
    {
        var buffer = new StreamTraceBuffer(100, enabled: false);
        var session = Guid.NewGuid();

        buffer.RangeOpen(session, "/view/a.mkv", "GET", 0, null, 10, null, null);
        buffer.Seek(session, 5);
        buffer.RangeEnd(session, ReadSession.EndReasonCode.Completed, 10);

        Assert.False(buffer.Enabled);
        Assert.Empty(buffer.GetSessionEvents(session));
        Assert.Empty(buffer.ListSessions());
    }

    [Fact]
    public void ListSessions_ReturnsNewestFirst()
    {
        var buffer = new StreamTraceBuffer(100);
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();
        buffer.RangeOpen(older, "/old", "GET", 0, 1, 10, null, null);
        Thread.Sleep(5);
        buffer.RangeOpen(newer, "/new", "GET", 0, 1, 10, null, null);

        var sessions = buffer.ListSessions();
        Assert.Equal(newer, sessions[0].SessionId);
        Assert.Equal(older, sessions[1].SessionId);
    }

    [Fact]
    public void EnableFor_UiSource_CapturesEventsUntilDisabled()
    {
        var buffer = new StreamTraceBuffer(100, enabled: false);
        Assert.False(buffer.Enabled);

        var status = buffer.EnableFor(TimeSpan.FromMinutes(15), 5_000, StreamTraceBuffer.SourceUi);
        Assert.True(status.Enabled);
        Assert.Equal(StreamTraceBuffer.SourceUi, status.Source);
        Assert.True(status.ExpiresAtUnixMs > 0);
        Assert.Equal(5_000, status.Capacity);

        var session = Guid.NewGuid();
        buffer.RangeOpen(session, "/view/a.mkv", "GET", 0, 1, 10, null, null);
        Assert.Single(buffer.GetSessionEvents(session));

        buffer.Disable();
        Assert.False(buffer.Enabled);
        Assert.Empty(buffer.ListSessions());
        Assert.Empty(buffer.GetSessionEvents(session));
    }

    [Fact]
    public void EnableFor_ClampsUiCapacityAndLeavesEnvCeilingHigher()
    {
        var ui = new StreamTraceBuffer(100, enabled: false);
        var uiStatus = ui.EnableFor(TimeSpan.FromMinutes(30), 999_999, StreamTraceBuffer.SourceUi);
        Assert.Equal(StreamTraceBuffer.UiMaxCapacity, uiStatus.Capacity);

        var env = new StreamTraceBuffer(100, enabled: false);
        var envStatus = env.EnableFor(TimeSpan.Zero, 150_000, StreamTraceBuffer.SourceEnv);
        Assert.Equal(150_000, envStatus.Capacity);
        Assert.Equal(0, envStatus.ExpiresAtUnixMs);
        Assert.True(env.Enabled);
        Assert.False(env.IsExpired);
    }

    [Fact]
    public void IsExpired_BecomesTrueAfterZeroTtlWindow()
    {
        var buffer = new StreamTraceBuffer(100, enabled: false);
        // A 1ms TTL is enough to expire without sleeping long in the test.
        buffer.EnableFor(TimeSpan.FromMilliseconds(1), 100, StreamTraceBuffer.SourceUi);
        Thread.Sleep(5);
        Assert.True(buffer.IsExpired);
        Assert.False(buffer.Enabled);

        buffer.Disable();
        Assert.False(buffer.IsExpired);
        Assert.False(buffer.Enabled);
    }

    [Fact]
    public void GetRecentEvents_ReturnsNewestWindowOldestFirst()
    {
        var buffer = new StreamTraceBuffer(100);
        var session = Guid.NewGuid();
        for (var i = 0; i < 10; i++)
            buffer.Seek(session, i);

        var recent = buffer.GetRecentEvents(3);
        Assert.Equal(3, recent.Count);
        Assert.True(recent[0].Sequence < recent[1].Sequence);
        Assert.True(recent[1].Sequence < recent[2].Sequence);
        Assert.Equal(10, recent[^1].Sequence);
    }
}
