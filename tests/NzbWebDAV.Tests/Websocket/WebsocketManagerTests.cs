using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using NzbWebDAV.Services.StreamTrace;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Tests.Websocket;

public class WebsocketManagerTests
{
    [Fact]
    public async Task SendMessage_DoesNotLetSlowSocketBlockFastSocket()
    {
        var manager = new WebsocketManager();
        manager.SimulateSubscribe(WebsocketTopic.LiveStats);
        using var slowSocket = new TestWebSocket(blockSends: true);
        using var fastSocket = new TestWebSocket();
        var detachSlow = manager.AttachAuthenticatedSocketForTests(slowSocket);
        var detachFast = manager.AttachAuthenticatedSocketForTests(fastSocket);

        try
        {
            var broadcast = manager.SendMessage(WebsocketTopic.LiveStats, "current");

            await broadcast.WaitAsync(TimeSpan.FromSeconds(1));
            await slowSocket.SendStarted.WaitAsync(TimeSpan.FromSeconds(1));
            await WaitUntil(() => fastSocket.Messages.Count == 1);

            Assert.Empty(slowSocket.Messages);
            Assert.Equal("current", Parse(fastSocket.Messages.Single()).Message);
        }
        finally
        {
            manager.SimulateUnsubscribe(WebsocketTopic.LiveStats);
            await detachSlow();
            await detachFast();
        }
    }

    [Fact]
    public async Task StateMessages_CoalescePerTopic()
    {
        var manager = new WebsocketManager();
        manager.SimulateSubscribe(WebsocketTopic.LiveStats);
        manager.SimulateSubscribe(WebsocketTopic.UsenetConnections);
        using var socket = new TestWebSocket(blockSends: true);
        var detach = manager.AttachAuthenticatedSocketForTests(socket);

        try
        {
            await manager.SendMessage(WebsocketTopic.LiveStats, "initial");
            await socket.SendStarted.WaitAsync(TimeSpan.FromSeconds(1));

            for (var i = 0; i < 10; i++)
                await manager.SendMessage(WebsocketTopic.LiveStats, $"stale-{i}");
            await manager.SendMessage(WebsocketTopic.LiveStats, "latest");
            await manager.SendMessage(WebsocketTopic.UsenetConnections, "connections");

            socket.ReleaseSends();
            await WaitUntil(() => socket.Messages.Count == 3);

            var messages = socket.Messages.Select(Parse).ToList();
            Assert.Equal(
                new[] { "initial", "latest" },
                messages.Where(x => x.Topic == WebsocketTopic.LiveStats.Name).Select(x => x.Message));
            Assert.Equal(
                ["connections"],
                messages.Where(x => x.Topic == WebsocketTopic.UsenetConnections.Name).Select(x => x.Message));
        }
        finally
        {
            manager.SimulateUnsubscribe(WebsocketTopic.LiveStats);
            manager.SimulateUnsubscribe(WebsocketTopic.UsenetConnections);
            await detach();
        }
    }

    [Fact]
    public async Task KeyedStateMessages_CoalesceToLatestValuePerItem()
    {
        var manager = new WebsocketManager();
        manager.SimulateSubscribe(WebsocketTopic.LiveStats);
        manager.SimulateSubscribe(WebsocketTopic.QueueItemProgress);
        manager.SimulateSubscribe(WebsocketTopic.HealthItemProgress);
        using var socket = new TestWebSocket(blockSends: true);
        var detach = manager.AttachAuthenticatedSocketForTests(socket);

        try
        {
            await manager.SendMessage(WebsocketTopic.LiveStats, "blocked");
            await socket.SendStarted.WaitAsync(TimeSpan.FromSeconds(1));

            await manager.SendMessage(WebsocketTopic.QueueItemProgress, "queue-a|10");
            await manager.SendMessage(WebsocketTopic.QueueItemProgress, "queue-b|20");
            await manager.SendMessage(WebsocketTopic.QueueItemProgress, "queue-a|90");
            await manager.SendMessage(WebsocketTopic.QueueItemProgress, "queue-b|100");
            await manager.SendMessage(WebsocketTopic.HealthItemProgress, "health-a|25");
            await manager.SendMessage(WebsocketTopic.HealthItemProgress, "health-b|50");
            await manager.SendMessage(WebsocketTopic.HealthItemProgress, "health-a|done");
            await manager.SendMessage(WebsocketTopic.HealthItemProgress, "health-b|done");

            socket.ReleaseSends();
            await WaitUntil(() => socket.Messages.Count == 5);

            var messages = socket.Messages.Select(Parse).ToList();
            Assert.Equal(
                ["queue-a|90", "queue-b|100"],
                messages
                    .Where(x => x.Topic == WebsocketTopic.QueueItemProgress.Name)
                    .Select(x => x.Message)
                    .Order());
            Assert.Equal(
                ["health-a|done", "health-b|done"],
                messages
                    .Where(x => x.Topic == WebsocketTopic.HealthItemProgress.Name)
                    .Select(x => x.Message)
                    .Order());
        }
        finally
        {
            manager.SimulateUnsubscribe(WebsocketTopic.LiveStats);
            manager.SimulateUnsubscribe(WebsocketTopic.QueueItemProgress);
            manager.SimulateUnsubscribe(WebsocketTopic.HealthItemProgress);
            await detach();
        }
    }

    [Fact]
    public async Task EventQueueOverflow_DropsOldestEventsAndKeepsSocketConnected()
    {
        var manager = new WebsocketManager();
        manager.SimulateSubscribe(WebsocketTopic.LiveStats);
        manager.SimulateSubscribe(WebsocketTopic.QueueItemAdded);
        using var socket = new TestWebSocket(blockSends: true);
        var detach = manager.AttachAuthenticatedSocketForTests(socket);

        try
        {
            await manager.SendMessage(WebsocketTopic.LiveStats, "blocked");
            await socket.SendStarted.WaitAsync(TimeSpan.FromSeconds(1));

            for (var i = 0; i <= 64; i++)
                await manager.SendMessage(WebsocketTopic.QueueItemAdded, $"event-{i}");

            socket.ReleaseSends();
            await WaitUntil(() => socket.Messages.Count == 65);

            Assert.False(socket.Aborted);
            Assert.Equal(1, manager.GetAuthenticatedSocketCount());
            var messages = socket.Messages.Select(Parse).ToList();
            Assert.Equal("blocked", messages[0].Message);
            Assert.Equal(
                Enumerable.Range(1, 64).Select(i => $"event-{i}"),
                messages.Skip(1).Select(x => x.Message));
        }
        finally
        {
            manager.SimulateUnsubscribe(WebsocketTopic.LiveStats);
            manager.SimulateUnsubscribe(WebsocketTopic.QueueItemAdded);
            await detach();
        }
    }

    [Fact]
    public async Task SendMessage_UpdatesLastMessageWithoutConnectedSockets()
    {
        var manager = new WebsocketManager();

        await manager.SendMessage(WebsocketTopic.LiveStats, "latest");

        Assert.Equal("latest", manager.PeekLastMessage(WebsocketTopic.LiveStats));
    }

    [Fact]
    public async Task SendMessage_SkipsSerializationWhenNoTopicSubscribers()
    {
        var manager = new WebsocketManager();
        using var socket = new TestWebSocket();
        var detach = manager.AttachAuthenticatedSocketForTests(socket);

        try
        {
            await manager.SendMessage(WebsocketTopic.LiveStats, "nobody-listening");
            await Task.Delay(100);

            Assert.Empty(socket.Messages);
            Assert.Equal("nobody-listening", manager.PeekLastMessage(WebsocketTopic.LiveStats));
            Assert.True(manager.SkippedPublishes > 0);
        }
        finally
        {
            await detach();
        }
    }

    [Fact]
    public async Task SendMessage_DeliversWhenTopicHasSubscribers()
    {
        var manager = new WebsocketManager();
        using var socket = new TestWebSocket();
        var detach = manager.AttachAuthenticatedSocketForTests(socket);
        manager.SimulateSubscribe(WebsocketTopic.LiveStats);

        try
        {
            await manager.SendMessage(WebsocketTopic.LiveStats, "with-subscriber");
            await WaitUntil(() => socket.Messages.Count == 1);

            var msg = Parse(socket.Messages.Single());
            Assert.Equal("with-subscriber", msg.Message);
        }
        finally
        {
            manager.SimulateUnsubscribe(WebsocketTopic.LiveStats);
            await detach();
        }
    }

    [Fact]
    public void HasSubscribers_IncrementAndDecrement()
    {
        var manager = new WebsocketManager();

        Assert.False(manager.HasSubscribers(WebsocketTopic.ActiveReads));

        manager.SimulateSubscribe(WebsocketTopic.ActiveReads);
        Assert.True(manager.HasSubscribers(WebsocketTopic.ActiveReads));

        manager.SimulateSubscribe(WebsocketTopic.ActiveReads);
        Assert.True(manager.HasSubscribers(WebsocketTopic.ActiveReads));

        manager.SimulateUnsubscribe(WebsocketTopic.ActiveReads);
        Assert.True(manager.HasSubscribers(WebsocketTopic.ActiveReads));

        manager.SimulateUnsubscribe(WebsocketTopic.ActiveReads);
        Assert.False(manager.HasSubscribers(WebsocketTopic.ActiveReads));
    }

    [Fact]
    public async Task StateTopicReplay_ServesLastMessage_EvenWhenSkippedSerialize()
    {
        var manager = new WebsocketManager();

        // Send a message with no subscribers — skips serialization but updates _lastMessage
        await manager.SendMessage(WebsocketTopic.LiveStats, "stale-value");
        Assert.Equal("stale-value", manager.PeekLastMessage(WebsocketTopic.LiveStats));

        // Now attach a socket with replayState — should still get the stale value
        using var socket = new TestWebSocket();
        var detach = manager.AttachAuthenticatedSocketForTests(socket, replayState: true);

        try
        {
            await WaitUntil(() => socket.Messages.Count == 1);

            var replay = Parse(socket.Messages.Single());
            Assert.Equal(WebsocketTopic.LiveStats.Name, replay.Topic);
            Assert.Equal("stale-value", replay.Message);
        }
        finally
        {
            await detach();
        }
    }

    [Fact]
    public async Task StreamTraceStatusTransitionWhileIdle_ReplaysToLateSubscriber()
    {
        var manager = new WebsocketManager();
        var broadcaster = new StreamTraceStatusBroadcaster(manager);

        await broadcaster.BroadcastAsync("""{"enabled":false}""");

        using var socket = new TestWebSocket();
        var detach = manager.AttachAuthenticatedSocketForTests(socket, replayState: true);

        try
        {
            await WaitUntil(() => socket.Messages.Count == 1);

            var replay = Parse(socket.Messages.Single());
            Assert.Equal(WebsocketTopic.StreamTracing.Name, replay.Topic);
            Assert.Equal("""{"enabled":false}""", replay.Message);
        }
        finally
        {
            await detach();
        }
    }

    [Fact]
    public async Task NewSocket_ReplaysLatestStateButNotEvents()
    {
        var manager = new WebsocketManager();
        manager.SimulateSubscribe(WebsocketTopic.LiveStats);
        manager.SimulateSubscribe(WebsocketTopic.QueueItemAdded);
        await manager.SendMessage(WebsocketTopic.LiveStats, "latest");
        await manager.SendMessage(WebsocketTopic.QueueItemAdded, "old-event");
        using var socket = new TestWebSocket();
        var detach = manager.AttachAuthenticatedSocketForTests(socket, replayState: true);

        try
        {
            await WaitUntil(() => socket.Messages.Count == 1);

            var replay = Parse(socket.Messages.Single());
            Assert.Equal(WebsocketTopic.LiveStats.Name, replay.Topic);
            Assert.Equal("latest", replay.Message);
        }
        finally
        {
            manager.SimulateUnsubscribe(WebsocketTopic.LiveStats);
            manager.SimulateUnsubscribe(WebsocketTopic.QueueItemAdded);
            await detach();
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private static ReceivedMessage Parse(string rawMessage)
    {
        using var document = JsonDocument.Parse(rawMessage);
        return new ReceivedMessage(
            document.RootElement.GetProperty("Topic").GetString()!,
            document.RootElement.GetProperty("Message").GetString()!);
    }

    private sealed record ReceivedMessage(string Topic, string Message);

    private sealed class TestWebSocket(bool blockSends = false) : WebSocket
    {
        private readonly object _messagesLock = new();
        private readonly List<string> _messages = [];
        private readonly TaskCompletionSource<bool> _sendStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseSends =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebSocketState _state = WebSocketState.Open;
        private int _aborted;

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messagesLock)
                    return _messages.ToList();
            }
        }

        public Task SendStarted => _sendStarted.Task;
        public bool Aborted => Volatile.Read(ref _aborted) != 0;
        public override WebSocketCloseStatus? CloseStatus { get; }
        public override string? CloseStatusDescription { get; }
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public void ReleaseSends()
        {
            _releaseSends.TrySetResult(true);
        }

        public override void Abort()
        {
            Interlocked.Exchange(ref _aborted, 1);
            _state = WebSocketState.Aborted;
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            _state = WebSocketState.Closed;
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Receive unexpectedly completed");
        }

        public override async Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            _sendStarted.TrySetResult(true);
            if (blockSends)
                await _releaseSends.Task.WaitAsync(cancellationToken);

            var message = Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count);
            lock (_messagesLock)
                _messages.Add(message);
        }
    }
}
