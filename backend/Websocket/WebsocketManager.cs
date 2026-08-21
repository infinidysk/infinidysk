using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Websocket;

public class WebsocketManager : IWebsocketPublisher
{
    private const int EventQueueCapacity = 64;
    private const int MaxSubscriptionMessageSize = 4096;
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);

    private readonly Dictionary<WebSocket, SocketSession> _sessions = new();
    private readonly Dictionary<WebsocketTopic, string> _lastMessage = new();
    private readonly Dictionary<WebsocketTopic, Dictionary<string, KeyedState>> _lastKeyedMessages = new();
    private readonly ConcurrentDictionary<WebsocketTopic, int> _subscriberCounts = new();
    private long _skippedPublishes;
    private long _stateSequence;

    /// <summary>
    /// Whether at least one downstream browser client is subscribed to the given topic.
    /// Publishers should check this before performing expensive work (DB queries, serialization).
    /// </summary>
    public bool HasSubscribers(WebsocketTopic topic) =>
        _subscriberCounts.TryGetValue(topic, out var count) && count > 0;

    /// <summary>Total number of publish calls skipped due to zero subscribers.</summary>
    public long SkippedPublishes => Interlocked.Read(ref _skippedPublishes);

    public async Task HandleRoute(HttpContext context)
    {
        if (context.WebSockets.IsWebSocketRequest)
        {
            using var webSocket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            if (!await Authenticate(webSocket).ConfigureAwait(false))
            {
                Log.Warning(
                    "Closing unauthenticated websocket connection from {RemoteIpAddress}",
                    context.Connection.RemoteIpAddress);
                await CloseUnauthorizedConnection(webSocket).ConfigureAwait(false);
                return;
            }

            var session = AddSocket(webSocket, replayState: true);
            Log.Debug(
                "Websocket client connected from {RemoteIpAddress}; {ConnectionCount} authenticated clients connected",
                context.Connection.RemoteIpAddress,
                GetAuthenticatedSocketCount());

            try
            {
                await ReceiveSubscriptions(session).ConfigureAwait(false);
            }
            finally
            {
                await RemoveSocket(session).ConfigureAwait(false);
            }

            Log.Debug(
                "Websocket client disconnected from {RemoteIpAddress}; {ConnectionCount} authenticated clients connected",
                context.Connection.RemoteIpAddress,
                GetAuthenticatedSocketCount());
        }
        else
        {
            context.Response.StatusCode = 400;
        }
    }

    /// <summary>
    /// Send a message to all authenticated websockets.
    /// </summary>
    /// <param name="topic">The topic of the message to send</param>
    /// <param name="message">The message to send</param>
    public Task SendMessage(WebsocketTopic topic, string message)
    {
        lock (_lastMessage)
        {
            if (topic.ReplayAllKeys && GetMessageKey(topic, message) is { } key)
            {
                if (!_lastKeyedMessages.TryGetValue(topic, out var messages))
                    _lastKeyedMessages[topic] = messages = new Dictionary<string, KeyedState>();

                messages[key] = new KeyedState(message, ++_stateSequence);
            }
            else
            {
                _lastMessage[topic] = message;
            }
        }
        List<SocketSession> sessions;
        lock (_sessions) sessions = _sessions.Values.ToList();
        if (sessions.Count == 0) return Task.CompletedTask;

        if (!HasSubscribers(topic))
        {
            Interlocked.Increment(ref _skippedPublishes);
            return Task.CompletedTask;
        }

        var bytes = SerializeMessage(topic, message);
        var messageKey = GetMessageKey(topic, message);
        foreach (var session in sessions)
            session.TryEnqueue(topic, messageKey, bytes);

        return Task.CompletedTask;
    }

    internal string? PeekLastMessage(WebsocketTopic topic)
    {
        lock (_lastMessage)
        {
            if (_lastMessage.TryGetValue(topic, out var message))
                return message;
            return _lastKeyedMessages.TryGetValue(topic, out var messages)
                ? messages.Values.MaxBy(entry => entry.Sequence)?.Message
                : null;
        }
    }

    internal void ClearKeyedState(WebsocketTopic topic)
    {
        if (!topic.ReplayAllKeys)
            throw new ArgumentException("Only topics that replay all keys can be cleared.", nameof(topic));

        lock (_lastMessage)
            _lastKeyedMessages.Remove(topic);
    }

    internal Func<Task> AttachAuthenticatedSocketForTests(WebSocket socket, bool replayState = false)
    {
        var session = AddSocket(socket, replayState);
        return () => RemoveSocket(session);
    }

    internal void SimulateSubscribe(WebsocketTopic topic)
    {
        _subscriberCounts.AddOrUpdate(topic, 1, (_, c) => c + 1);
    }

    internal void SimulateUnsubscribe(WebsocketTopic topic)
    {
        _subscriberCounts.AddOrUpdate(topic, 0, (_, c) => Math.Max(0, c - 1));
    }

    /// <summary>
    /// Ensure a websocket sends a valid api key.
    /// </summary>
    /// <param name="socket">The websocket to authenticate.</param>
    /// <returns>True if authenticated, False otherwise.</returns>
    private static async Task<bool> Authenticate(WebSocket socket)
    {
        var apiKey = await ReceiveAuthToken(socket).ConfigureAwait(false);
        return apiKey.FixedTimeEquals(EnvironmentUtil.GetRequiredVariable("FRONTEND_BACKEND_API_KEY"));
    }

    /// <summary>
    /// Receive frames from the connected relay, parsing subscription messages. Waits until
    /// the socket disconnects or the application shuts down. Malformed messages are logged
    /// and tolerated — the connection is never dropped over a parse error.
    /// </summary>
    private async Task ReceiveSubscriptions(SocketSession session)
    {
        var buffer = new byte[MaxSubscriptionMessageSize];
        using var messageBuffer = new MemoryStream(MaxSubscriptionMessageSize);
        var discardCurrentMessage = false;
        try
        {
            while (true)
            {
                var result = await session.Socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer), SigtermUtil.GetCancellationToken()).ConfigureAwait(false);

                if (result.CloseStatus is not null)
                {
                    await session.Socket.CloseAsync(
                        result.CloseStatus.Value, result.CloseStatusDescription, CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    discardCurrentMessage = true;
                }
                else if (!discardCurrentMessage)
                {
                    if (messageBuffer.Length + result.Count > MaxSubscriptionMessageSize)
                    {
                        discardCurrentMessage = true;
                        messageBuffer.SetLength(0);
                        Log.Debug(
                            "Ignoring websocket subscription message larger than {MaxBytes} bytes",
                            MaxSubscriptionMessageSize);
                    }
                    else
                    {
                        await messageBuffer.WriteAsync(buffer.AsMemory(0, result.Count), SigtermUtil.GetCancellationToken()).ConfigureAwait(false);
                    }
                }

                if (!result.EndOfMessage) continue;

                if (!discardCurrentMessage)
                {
                    var text = Encoding.UTF8.GetString(
                        messageBuffer.GetBuffer(), 0, checked((int)messageBuffer.Length));
                    ProcessSubscriptionMessage(session, text);
                }

                messageBuffer.SetLength(0);
                discardCurrentMessage = false;
            }
        }
        catch (OperationCanceledException)
        {
            if (session.Socket.State == WebSocketState.Open)
                await session.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server shutting down", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Warning(e, "Websocket receive loop failed");
        }
    }

    private void ProcessSubscriptionMessage(SocketSession session, string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            if (root.TryGetProperty("sub", out var subArray) && subArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var name in subArray.EnumerateArray().Select(element => element.GetString()))
                {
                    if (name is not null && WebsocketTopic.TryGetByName(name, out var topic) && topic is not null
                        && session.AddSubscription(topic))
                    {
                        _subscriberCounts.AddOrUpdate(topic, 1, (_, c) => c + 1);
                        ReplayStateForNewSubscription(session, topic);
                    }
                }
            }

            if (root.TryGetProperty("unsub", out var unsubArray) && unsubArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var name in unsubArray.EnumerateArray().Select(element => element.GetString()))
                {
                    if (name is not null && WebsocketTopic.TryGetByName(name, out var topic) && topic is not null
                        && session.RemoveSubscription(topic))
                    {
                        _subscriberCounts.AddOrUpdate(topic, 0, (_, c) => Math.Max(0, c - 1));
                    }
                }
            }
        }
        catch (JsonException)
        {
            Log.Debug("Ignoring malformed subscription message from websocket client");
        }
    }

    private void ReplayStateForNewSubscription(SocketSession session, WebsocketTopic topic)
    {
        if (topic.Type != WebsocketTopic.TopicType.State) return;

        lock (_lastMessage)
        {
            EnqueueReplayState(session, topic);
        }
    }

    private SocketSession AddSocket(WebSocket socket, bool replayState = false)
    {
        var session = new SocketSession(socket);
        session.DrainTask = DrainSocket(session);

        if (!replayState)
        {
            lock (_sessions)
                _sessions.Add(socket, session);
            return session;
        }

        lock (_lastMessage)
        {
            lock (_sessions)
                _sessions.Add(socket, session);

            foreach (var topic in _lastMessage.Keys.Concat(_lastKeyedMessages.Keys).Distinct())
                if (topic.Type == WebsocketTopic.TopicType.State)
                    EnqueueReplayState(session, topic);
        }

        return session;
    }

    private async Task DrainSocket(SocketSession session)
    {
        try
        {
            while (await session.WaitForWork().ConfigureAwait(false))
            {
                while (true)
                {
                    var stateMessages = session.TakePendingState();
                    var eventMessages = session.TakePendingEvents();
                    if (stateMessages.Count == 0 && eventMessages.Count == 0) break;

                    if (session.TryTakeDroppedEventMessageCount(out var droppedEventMessageCount))
                    {
                        Log.Warning(
                            "Websocket client is consuming events too slowly; dropped {Count} event messages",
                            droppedEventMessageCount);
                    }

                    foreach (var message in stateMessages)
                        await SendToSocket(session, message).ConfigureAwait(false);
                    foreach (var message in eventMessages)
                        await SendToSocket(session, message).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (session.CancellationToken.IsCancellationRequested)
        {
            // Expected when the socket disconnects or the application shuts down.
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Debug(e, "Failed to send message to websocket");
            AbortSocket(session);
        }
    }

    private static async Task SendToSocket(SocketSession session, ArraySegment<byte> message)
    {
        if (session.Socket.State != WebSocketState.Open)
            throw new WebSocketException("Websocket is no longer open");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(session.CancellationToken);
        timeout.CancelAfter(SendTimeout);
        await session.Socket.SendAsync(message, WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
    }

    private async Task RemoveSocket(SocketSession session)
    {
        lock (_sessions)
        {
            if (_sessions.TryGetValue(session.Socket, out var current) && ReferenceEquals(current, session))
                _sessions.Remove(session.Socket);
        }

        foreach (var topic in session.TakeAllSubscriptions())
            _subscriberCounts.AddOrUpdate(topic, 0, (_, c) => Math.Max(0, c - 1));

        session.Stop();
        await session.DrainTask.ConfigureAwait(false);
        session.Dispose();
    }

    private void AbortSocket(SocketSession session)
    {
        lock (_sessions)
        {
            if (_sessions.TryGetValue(session.Socket, out var current) && ReferenceEquals(current, session))
                _sessions.Remove(session.Socket);
        }

        if (!session.Stop()) return;

        try { session.Socket.Abort(); }
        catch (ObjectDisposedException) { /* socket already disposed */ }
    }

    /// <summary>
    /// Receive an authentication token from a connected websocket.
    /// With timeout after five seconds.
    /// </summary>
    /// <param name="socket">The websocket to receive from.</param>
    /// <returns>The authentication token. Or null if none provided.</returns>
    private static async Task<string?> ReceiveAuthToken(WebSocket socket)
    {
        try
        {
            var buffer = new byte[1024];
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(SigtermUtil.GetCancellationToken());
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token).ConfigureAwait(false);
            return result.MessageType == WebSocketMessageType.Text
                ? Encoding.UTF8.GetString(buffer, 0, result.Count)
                : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Close a websocket connection as unauthorized.
    /// </summary>
    /// <param name="socket">The websocket whose connection to close.</param>
    private static async Task CloseUnauthorizedConnection(WebSocket socket)
    {
        if (socket.State == WebSocketState.Open)
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Unauthorized", CancellationToken.None).ConfigureAwait(false);
    }

    internal int GetAuthenticatedSocketCount()
    {
        lock (_sessions)
            return _sessions.Count;
    }

    private static ArraySegment<byte> SerializeMessage(WebsocketTopic topic, string message)
    {
        var topicMessage = new TopicMessage(topic, message);
        return new ArraySegment<byte>(Encoding.UTF8.GetBytes(topicMessage.ToJson()));
    }

    private static string? GetMessageKey(WebsocketTopic topic, string message)
    {
        if (!topic.IsKeyed) return null;
        var separator = message.IndexOf('|', StringComparison.Ordinal);
        return separator > 0 ? message[..separator] : null;
    }

    private void EnqueueReplayState(SocketSession session, WebsocketTopic topic)
    {
        if (topic.ReplayAllKeys &&
            _lastKeyedMessages.TryGetValue(topic, out var keyedMessages))
        {
            foreach (var keyedMessage in keyedMessages.Values.OrderBy(entry => entry.Sequence))
                session.TryEnqueue(
                    topic,
                    GetMessageKey(topic, keyedMessage.Message),
                    SerializeMessage(topic, keyedMessage.Message));
            return;
        }

        if (_lastMessage.TryGetValue(topic, out var message))
            session.TryEnqueue(topic, GetMessageKey(topic, message), SerializeMessage(topic, message));
    }

    private sealed class TopicMessage(WebsocketTopic topic, string message)
    {
        public string Topic { get; } = topic.Name;
        public string Message { get; } = message;
    }

    private sealed record KeyedState(string Message, long Sequence);

    private sealed class SocketSession : IDisposable
    {
        private readonly object _stateLock = new();
        private readonly Dictionary<WebsocketTopic, ArraySegment<byte>> _pendingState = new();
        private readonly Dictionary<(WebsocketTopic Topic, string Key), ArraySegment<byte>> _pendingKeyedState = new();
        private readonly Channel<ArraySegment<byte>> _eventMessages;
        private readonly Channel<bool> _workSignal =
            Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false
            });
        private readonly CancellationTokenSource _cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(SigtermUtil.GetCancellationToken());
        private readonly HashSet<WebsocketTopic> _subscriptions = new();
        private long _droppedEventMessageCount;
        private long _lastDroppedEventWarningTimestamp;
        private int _stopped;

        public SocketSession(WebSocket socket)
        {
            Socket = socket;
            _eventMessages = Channel.CreateBounded<ArraySegment<byte>>(
                new BoundedChannelOptions(EventQueueCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false
                },
                _ => Interlocked.Increment(ref _droppedEventMessageCount));
        }

        public WebSocket Socket { get; }
        public CancellationToken CancellationToken => _cancellation.Token;
        public Task DrainTask { get; set; } = Task.CompletedTask;

        public bool AddSubscription(WebsocketTopic topic)
        {
            lock (_stateLock)
                return _subscriptions.Add(topic);
        }

        public bool RemoveSubscription(WebsocketTopic topic)
        {
            lock (_stateLock)
                return _subscriptions.Remove(topic);
        }

        public List<WebsocketTopic> TakeAllSubscriptions()
        {
            lock (_stateLock)
            {
                var topics = _subscriptions.ToList();
                _subscriptions.Clear();
                return topics;
            }
        }

        public bool TryEnqueue(WebsocketTopic topic, string? messageKey, ArraySegment<byte> message)
        {
            if (Volatile.Read(ref _stopped) != 0) return false;

            if (topic.Type == WebsocketTopic.TopicType.State)
            {
                lock (_stateLock)
                {
                    if (_stopped != 0) return false;
                    if (topic.IsKeyed && messageKey is not null)
                        _pendingKeyedState[(topic, messageKey)] = message;
                    else
                        _pendingState[topic] = message;
                }
            }
            else if (!_eventMessages.Writer.TryWrite(message))
            {
                return false;
            }

            _workSignal.Writer.TryWrite(true);
            return true;
        }

        public async ValueTask<bool> WaitForWork()
        {
            return await _workSignal.Reader.WaitToReadAsync(CancellationToken).ConfigureAwait(false)
                   && _workSignal.Reader.TryRead(out _);
        }

        public List<ArraySegment<byte>> TakePendingState()
        {
            lock (_stateLock)
            {
                var messages = new List<ArraySegment<byte>>(
                    _pendingState.Count + _pendingKeyedState.Count);
                messages.AddRange(_pendingState.Values);
                messages.AddRange(_pendingKeyedState.Values);
                _pendingState.Clear();
                _pendingKeyedState.Clear();
                return messages;
            }
        }

        public List<ArraySegment<byte>> TakePendingEvents()
        {
            var messages = new List<ArraySegment<byte>>(EventQueueCapacity);
            while (messages.Count < EventQueueCapacity && _eventMessages.Reader.TryRead(out var message))
                messages.Add(message);
            return messages;
        }

        public bool TryTakeDroppedEventMessageCount(out long count)
        {
            count = 0;
            if (Volatile.Read(ref _droppedEventMessageCount) == 0) return false;

            var now = Stopwatch.GetTimestamp();
            var lastWarning = Volatile.Read(ref _lastDroppedEventWarningTimestamp);
            if (lastWarning != 0 &&
                Stopwatch.GetElapsedTime(lastWarning, now) < TimeSpan.FromMinutes(1))
                return false;

            if (Interlocked.CompareExchange(ref _lastDroppedEventWarningTimestamp, now, lastWarning) != lastWarning)
                return false;

            count = Interlocked.Exchange(ref _droppedEventMessageCount, 0);
            return count > 0;
        }

        public bool Stop()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return false;

            _eventMessages.Writer.TryComplete();
            _workSignal.Writer.TryComplete();
            _cancellation.Cancel();
            return true;
        }

        public void Dispose()
        {
            _cancellation.Dispose();
        }
    }
}
