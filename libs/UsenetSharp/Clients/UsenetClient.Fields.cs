using System.IO.Pipelines;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using UsenetSharp.Concurrency;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    private readonly UsenetClientOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly PipeOptions _decodedBodyPipeOptions;
    private TcpClient? _tcpClient;
    private Stream? _stream;
    private NntpLineReader? _reader;
    private readonly AsyncSemaphore _commandLock = new(1);
    private CancellationTokenSource _connectionCts = new();
    private readonly object _connectionCtsLock = new();
    private readonly object _stateLock = new();
    private int _connectionState;
    private long _bufferedDecodedBodyBytes;
    private volatile ExceptionDispatchInfo? _backgroundException;

    /// <summary>Exposes the connected socket for deterministic socket-option tests.</summary>
    internal Socket? ConnectedSocket => _tcpClient?.Client;
}
