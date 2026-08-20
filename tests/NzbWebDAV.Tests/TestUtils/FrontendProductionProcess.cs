using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NzbWebDAV.Tests.TestUtils;

internal sealed class FrontendProductionProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _output = new();

    private FrontendProductionProcess(Process process, Uri baseAddress)
    {
        _process = process;
        BaseAddress = baseAddress;
    }

    public Uri BaseAddress { get; }

    public static async Task<FrontendProductionProcess> StartAsync(
        Uri backendUrl,
        string apiKey,
        string configPath,
        CancellationToken cancellationToken = default)
    {
        if (!RepoPaths.FrontendProductionBuildExists())
        {
            throw new InvalidOperationException(
                "Frontend production build is missing. Run `npm run build && npm run build:server` in frontend/.");
        }

        var port = GetFreePort();
        var start = new ProcessStartInfo
        {
            FileName = "node",
            Arguments = "dist-node/server.js",
            WorkingDirectory = RepoPaths.FrontendRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["NODE_ENV"] = "production";
        start.Environment["PORT"] = port.ToString();
        start.Environment["BACKEND_URL"] = backendUrl.ToString().TrimEnd('/');
        start.Environment["FRONTEND_BACKEND_API_KEY"] = apiKey;
        start.Environment["CONFIG_PATH"] = configPath;
        start.Environment["LOG_LEVEL"] = "error";

        var process = Process.Start(start)
            ?? throw new InvalidOperationException("Failed to start the frontend production server.");
        var host = new FrontendProductionProcess(process, new Uri($"http://127.0.0.1:{port}"));
        process.OutputDataReceived += (_, args) => host.AppendOutput("stdout", args.Data);
        process.ErrorDataReceived += (_, args) => host.AppendOutput("stderr", args.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await host.WaitForHealthAsync(cancellationToken).ConfigureAwait(false);
            return host;
        }
        catch (Exception)
        {
            await host.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public HttpClient CreateClient()
    {
        return new HttpClient { BaseAddress = BaseAddress, Timeout = TimeSpan.FromSeconds(15) };
    }

    private async Task WaitForHealthAsync(CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            while (true)
            {
                if (_process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"Frontend process exited {_process.ExitCode}. Output:{Environment.NewLine}{_output}");
                }

                try
                {
                    using var response = await client.GetAsync("/healthz", timeout.Token).ConfigureAwait(false);
                    if (response.StatusCode == HttpStatusCode.OK)
                        return;
                }
                catch (HttpRequestException)
                {
                    // Process is still binding.
                }

                await Task.Delay(50, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Frontend /healthz did not become ready. Output:{Environment.NewLine}{_output}");
        }
    }

    private void AppendOutput(string stream, string? line)
    {
        if (string.IsNullOrEmpty(line))
            return;
        lock (_output)
            _output.Append(stream).Append(": ").AppendLine(line);
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync()
    {
        using (_process)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
            }
            catch (InvalidOperationException)
            {
                // Already exited between HasExited and Kill.
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Process is gone or cannot be signalled.
            }
            catch (NotSupportedException)
            {
                // Entire process tree is unsupported on this platform.
            }
            catch (TimeoutException)
            {
                // Timed out waiting for exit; Dispose still runs.
            }
            catch (OperationCanceledException)
            {
                // Timed out waiting for exit; Dispose still runs.
            }
        }
    }
}
