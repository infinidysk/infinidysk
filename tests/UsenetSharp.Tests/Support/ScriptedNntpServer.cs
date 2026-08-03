using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace UsenetSharpTest.Support;

internal sealed class ScriptedNntpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<string, StreamWriter, CancellationToken, Task>? _commandHandler;
    private readonly Func<StreamReader, StreamWriter, CancellationToken, Task>? _connectionHandler;
    private readonly string? _greeting;
    private readonly bool _useTls;
    private readonly X509Certificate2? _certificate;
    private readonly Task _acceptLoop;

    public ScriptedNntpServer(Func<string, StreamWriter, CancellationToken, Task> commandHandler)
        : this(commandHandler, null, "200 scripted server ready")
    {
    }

    private ScriptedNntpServer(
        Func<string, StreamWriter, CancellationToken, Task>? commandHandler,
        Func<StreamReader, StreamWriter, CancellationToken, Task>? connectionHandler,
        string? greeting = "200 scripted server ready",
        bool useTls = false)
    {
        _commandHandler = commandHandler;
        _connectionHandler = connectionHandler;
        _greeting = greeting;
        _useTls = useTls;
        _certificate = useTls ? CreateSelfSignedCertificate() : null;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync();
    }

    public static ScriptedNntpServer StartConnectionScript(
        Func<StreamReader, StreamWriter, CancellationToken, Task> connectionHandler)
    {
        return new ScriptedNntpServer(null, connectionHandler);
    }

    public static ScriptedNntpServer StartTlsConnectionScript(
        Func<StreamReader, StreamWriter, CancellationToken, Task> connectionHandler)
    {
        return new ScriptedNntpServer(null, connectionHandler, useTls: true);
    }

    public static ScriptedNntpServer WithGreeting(
        string greeting,
        Func<string, StreamWriter, CancellationToken, Task>? commandHandler = null)
    {
        return new ScriptedNntpServer(
            commandHandler ?? ((_, _, _) => Task.CompletedTask),
            null,
            greeting);
    }

    public int Port { get; }
    public ConcurrentQueue<string> Commands { get; } = new();

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = HandleClientAsync(client);
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_cts.IsCancellationRequested)
        {
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        await using (var networkStream = client.GetStream())
        await using (var stream = await AuthenticateTlsIfNeededAsync(networkStream))
        using (var reader = new StreamReader(stream, Encoding.Latin1, leaveOpen: true))
        await using (var writer = new StreamWriter(stream, Encoding.Latin1, leaveOpen: true)
        { AutoFlush = true, NewLine = "\r\n" })
        {
            if (_greeting != null)
            {
                await writer.WriteLineAsync(_greeting);
            }

            if (_connectionHandler != null)
            {
                await _connectionHandler(reader, writer, _cts.Token);
                return;
            }

            while (!_cts.IsCancellationRequested)
            {
                var command = await reader.ReadLineAsync(_cts.Token);
                if (command == null)
                {
                    return;
                }

                Commands.Enqueue(command);
                if (command == "QUIT")
                {
                    await writer.WriteLineAsync("205 Connection closing");
                    return;
                }

                await _commandHandler!(command, writer, _cts.Token);
            }
        }
    }

    private async Task<Stream> AuthenticateTlsIfNeededAsync(NetworkStream networkStream)
    {
        if (!_useTls) return networkStream;

        var sslStream = new SslStream(networkStream, leaveInnerStreamOpen: false);
        await sslStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = _certificate!,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        }, _cts.Token);
        return sslStream;
    }

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=scripted-nntp.invalid",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        try
        {
            await _acceptLoop;
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
        _certificate?.Dispose();
    }
}
