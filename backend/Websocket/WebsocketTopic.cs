namespace NzbWebDAV.Websocket;

public class WebsocketTopic
{
    private static readonly Dictionary<string, WebsocketTopic> ByName = new(StringComparer.Ordinal);

    // Stateful topics
    public static readonly WebsocketTopic UsenetConnections = new("cxs", TopicType.State, isKeyed: true, replayAllKeys: true);
    public static readonly WebsocketTopic ActiveReads = new("ar", TopicType.State);
    public static readonly WebsocketTopic SymlinkTaskProgress = new("stp", TopicType.State);
    public static readonly WebsocketTopic CleanupTaskProgress = new("ctp", TopicType.State);
    public static readonly WebsocketTopic PruneCompletedHistoryTaskProgress = new("pchp", TopicType.State);
    public static readonly WebsocketTopic RenameWindowsInvalidPathsProgress = new("rwip", TopicType.State);
    public static readonly WebsocketTopic StrmToSymlinksTaskProgress = new("st2sy", TopicType.State);
    public static readonly WebsocketTopic RecreateStrmTaskProgress = new("rstm", TopicType.State);
    public static readonly WebsocketTopic QueueItemStatus = new("qs", TopicType.State, isKeyed: true);
    public static readonly WebsocketTopic QueueItemProgress = new("qp", TopicType.State, isKeyed: true);
    public static readonly WebsocketTopic QueueItemProviders = new("qpv", TopicType.State, isKeyed: true);
    public static readonly WebsocketTopic HealthItemStatus = new("hs", TopicType.State, isKeyed: true);
    public static readonly WebsocketTopic HealthItemProgress = new("hp", TopicType.State, isKeyed: true);
    public static readonly WebsocketTopic LiveStats = new("ls", TopicType.State);
    public static readonly WebsocketTopic BenchmarkProgress = new("bench", TopicType.State);
    public static readonly WebsocketTopic StreamTracing = new("strt", TopicType.State);

    // Eventful topics
    public static readonly WebsocketTopic QueueItemAdded = new("qa", TopicType.Event);
    public static readonly WebsocketTopic QueueItemRemoved = new("qr", TopicType.Event);
    public static readonly WebsocketTopic QueueItemMoved = new("qm", TopicType.Event);
    public static readonly WebsocketTopic QueueOrderChanged = new("qo", TopicType.Event);
    public static readonly WebsocketTopic HistoryItemAdded = new("ha", TopicType.Event);
    public static readonly WebsocketTopic HistoryItemRemoved = new("hr", TopicType.Event);
    public static readonly WebsocketTopic LogEntryAdded = new("log", TopicType.Event);

    // Migration progress topic
    public static readonly WebsocketTopic UsenetFileToBlobstoreMigrationProgress = new("uftbmp", TopicType.State);

    // Database backup / restore progress
    public static readonly WebsocketTopic DatabaseBackupTaskProgress = new("dbbk", TopicType.State);
    public static readonly WebsocketTopic DatabaseRestoreTaskProgress = new("dbrs", TopicType.State);

    public readonly string Name;
    public readonly TopicType Type;
    public readonly bool IsKeyed;
    public readonly bool ReplayAllKeys;

    private WebsocketTopic(string name, TopicType type, bool isKeyed = false, bool replayAllKeys = false)
    {
        Name = name;
        Type = type;
        IsKeyed = isKeyed;
        ReplayAllKeys = replayAllKeys;
        ByName[name] = this;
    }

    public static bool TryGetByName(string name, out WebsocketTopic? topic) =>
        ByName.TryGetValue(name, out topic);

    public enum TopicType
    {
        State,
        Event
    }
}
