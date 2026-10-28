using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using NzbWebDAV.Exceptions;
using Serilog;

namespace NzbWebDAV.Services.Catalog;

/// <summary>
/// The process boundary for the compatibility encoder. Tests substitute this seam to prove
/// cancellation, retry, cache, and manifest behavior without launching FFmpeg.
/// </summary>
public interface IFFmpegRunner
{
    /// <summary>Media duration reported by the source, or null when it cannot be probed.</summary>
    Task<TimeSpan?> ProbeDurationAsync(string sourceUri, CancellationToken ct);

    /// <summary>
    /// Encodes <paramref name="segmentCount"/> consecutive six-second MPEG-TS segments from
    /// <paramref name="start"/> with one continuous encoder, invoking
    /// <paramref name="onSegmentPublished"/> as each segment file is finished.
    /// </summary>
    Task TranscodeRangeAsync(
        string sourceUri,
        TimeSpan start,
        int segmentCount,
        string outputDirectory,
        Action<int, string> onSegmentPublished,
        CancellationToken ct);
}

/// <summary>
/// Production <see cref="IFFmpegRunner"/>: runs <c>ffprobe</c>/<c>ffmpeg</c> from PATH with
/// an argument list (never a shell), restricted protocols, and a hard process-tree kill on
/// cancellation or timeout. Arguments, signed URLs, and raw stderr are never logged.
/// </summary>
public sealed class FFmpegRunner : IFFmpegRunner
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SegmentTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Only HTTP(S) transports are reachable from the encoder.</summary>
    private const string AllowedProtocols = "http,https,tcp,tls";

    private const string ScaleFilter = "scale='trunc(min(1920,iw)/2)*2':-2";
    private const string KeyFrameExpression = "expr:gte(t,n_forced*6)";

    /// <summary>
    /// Constant offset added to every segment's timestamps. It keeps the AAC priming
    /// non-negative while giving segments one absolute timeline, so the manifest needs no
    /// discontinuity tags and the player never re-initialises at a boundary.
    /// </summary>
    private const double TimestampBaseSeconds = 1.0;

    public async Task<TimeSpan?> ProbeDurationAsync(string sourceUri, CancellationToken ct)
    {
        var arguments = new[]
        {
            "-v", "error",
            "-protocol_whitelist", AllowedProtocols,
            "-probesize", "10000000",
            "-analyzeduration", "10000000",
            "-show_entries", "format=duration:stream=codec_type,duration",
            "-of", "json",
            sourceUri,
        };

        var output = await RunAsync("ffprobe", arguments, ProbeTimeout, captureOutput: true, ct)
            .ConfigureAwait(false);
        return ParseDuration(output);
    }

    public Task TranscodeRangeAsync(
        string sourceUri,
        TimeSpan start,
        int segmentCount,
        string outputDirectory,
        Action<int, string> onSegmentPublished,
        CancellationToken ct)
    {
        var duration = TimeSpan.FromSeconds(segmentCount * CatalogHlsManifest.SegmentSeconds);
        var arguments = BuildRangeArguments(sourceUri, start, duration, segmentCount, outputDirectory);
        // The encoder is a background producer on a machine that is also decoding for
        // playback. A lower scheduling priority lets the browser win through the initial
        // buffer fill instead of competing with it for every core.
        return RunRangeAsync(arguments, outputDirectory, segmentCount, onSegmentPublished, ct);
    }

    /// <summary>
    /// Encoder arguments for one continuous run. FFmpeg's own HLS muxer cuts the stream at
    /// forced keyframes, so every segment shares one encoder and one timestamp timeline:
    /// audio and video meet exactly at each boundary, with no per-segment priming hole and
    /// no decoder re-initialisation. <c>-output_ts_offset</c> places the run on the absolute
    /// timeline, so segments from separate runs (after a seek) still splice together.
    /// </summary>
    internal static IReadOnlyList<string> BuildRangeArguments(
        string sourceUri,
        TimeSpan start,
        TimeSpan duration,
        int segmentCount,
        string outputDirectory)
    {
        var offset = start + TimeSpan.FromSeconds(TimestampBaseSeconds);
        return new[]
        {
            "-nostdin",
            "-y",
            "-v", "error",
            "-protocol_whitelist", AllowedProtocols,
            "-ss", FormatSeconds(start),
            "-i", sourceUri,
            "-t", FormatSeconds(duration),
            "-map", "0:v:0",
            "-map", "0:a:0?",
            "-sn",
            "-dn",
            "-vf", ScaleFilter,
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-crf", "23",
            "-pix_fmt", "yuv420p",
            "-force_key_frames", KeyFrameExpression,
            "-c:a", "aac",
            "-ar", "48000",
            "-ac", "2",
            "-b:a", "160k",
            "-muxdelay", "0",
            "-muxpreload", "0",
            "-output_ts_offset", FormatSeconds(offset),
            "-avoid_negative_ts", "disabled",
            "-f", "hls",
            "-hls_time", CatalogHlsManifest.SegmentSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-hls_list_size", "0",
            "-hls_playlist_type", "event",
            "-hls_flags", "independent_segments",
            "-hls_segment_filename", Path.Join(outputDirectory, "%d.ts"),
            Path.Join(outputDirectory, "index.m3u8"),
        };
    }

    /// <summary>
    /// Runs one encoder and reports each segment as soon as its file stops growing. FFmpeg
    /// closes a segment before opening the next, so a completed file is safe to serve while
    /// the run continues.
    /// </summary>
    private static async Task RunRangeAsync(
        IReadOnlyList<string> arguments,
        string outputDirectory,
        int segmentCount,
        Action<int, string> onSegmentPublished,
        CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(SegmentTimeout * Math.Max(1, segmentCount));
        var encoder = RunAsync("ffmpeg", arguments, Timeout.InfiniteTimeSpan, captureOutput: false,
            linked.Token, lowPriority: true);

        try
        {
            for (var index = 0; index < segmentCount; index++)
            {
                var path = Path.Join(outputDirectory, $"{index}.ts");
                await WaitForCompletedFileAsync(path, encoder, linked.Token).ConfigureAwait(false);
                onSegmentPublished(index, path);
            }

            await encoder.ConfigureAwait(false);
        }
        finally
        {
            if (!encoder.IsCompleted) await linked.CancelAsync().ConfigureAwait(false);
            try { await encoder.ConfigureAwait(false); }
            catch { /* published through the run */ }
        }
    }

    /// <summary>
    /// Polls a segment file until FFmpeg has closed it. A file whose size stops changing for
    /// two consecutive checks is complete; the next segment appearing is an even stronger
    /// signal, so both are accepted.
    /// </summary>
    private static async Task WaitForCompletedFileAsync(
        string path,
        Task encoder,
        CancellationToken ct)
    {
        var stableBytes = -1L;
        var stableChecks = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (encoder.IsFaulted || encoder.IsCanceled) await encoder.ConfigureAwait(false);

            long size = -1;
            try
            {
                if (File.Exists(path)) size = new FileInfo(path).Length;
            }
            catch (IOException)
            {
                // FFmpeg is mid-write; try again on the next tick.
            }

            if (size > 0 && size == stableBytes)
            {
                stableChecks++;
                if (stableChecks >= 2) return;
            }
            else
            {
                stableChecks = 0;
                stableBytes = size;
            }

            if (encoder.IsCompleted && size > 0) return;
            await Task.Delay(FilePollInterval, ct).ConfigureAwait(false);
        }
    }

    private static readonly TimeSpan FilePollInterval = TimeSpan.FromMilliseconds(50);

    private static async Task<string> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        bool captureOutput,
        CancellationToken ct,
        bool lowPriority = false)
    {
        var startInfo = new ProcessStartInfo(lowPriority ? "nice" : fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (lowPriority)
        {
            // Busybox/coreutils `nice` is part of the runtime image.
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add("10");
            startInfo.ArgumentList.Add(fileName);
        }
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw CatalogException.Provider("The compatibility encoder is unavailable on this server.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        try
        {
            var stderr = process.StandardError.ReadToEndAsync(linked.Token);
            var stdout = process.StandardOutput.ReadToEndAsync(linked.Token);
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            var stdoutText = await stdout.ConfigureAwait(false);
            // Drained but never logged: provider internals and signed URLs appear here.
            _ = await stderr.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                // Exit code only: argument lists carry signed URLs and stderr carries provider
                // internals, so neither is safe to log.
                Log.Debug("{Tool} exited with code {ExitCode} while preparing a compatibility segment.",
                    fileName, process.ExitCode);
                throw CatalogException.Provider("The compatibility stream could not be prepared.");
            }

            return captureOutput ? stdoutText : string.Empty;
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            if (ct.IsCancellationRequested) throw;
            throw CatalogException.Provider("The compatibility encoder timed out.", TimeSpan.FromSeconds(5));
        }
        finally
        {
            KillTree(process);
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or AggregateException)
        {
            // The process already exited, or a child vanished first; nothing to clean up.
        }
    }

    private static string FormatSeconds(TimeSpan value)
        => value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// Uses the container duration, falling back to the longest reported stream duration.
    /// A missing, non-finite, or non-positive value returns null so the caller can answer 422.
    /// </summary>
    internal static TimeSpan? ParseDuration(string? probeJson)
    {
        if (string.IsNullOrWhiteSpace(probeJson)) return null;

        try
        {
            using var document = JsonDocument.Parse(probeJson);
            var root = document.RootElement;

            if (root.TryGetProperty("format", out var format)
                && format.TryGetProperty("duration", out var formatDuration)
                && formatDuration.ValueKind == JsonValueKind.String
                && TryParseSeconds(formatDuration.GetString()) is { } fromFormat)
            {
                return fromFormat;
            }

            if (!root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
                return null;

            TimeSpan? longest = null;
            foreach (var stream in streams.EnumerateArray())
            {
                if (!stream.TryGetProperty("duration", out var duration)
                    || duration.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                if (TryParseSeconds(duration.GetString()) is { } value && (longest is null || value > longest))
                    longest = value;
            }

            return longest;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TimeSpan? TryParseSeconds(string? raw)
    {
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            return null;
        if (!double.IsFinite(seconds) || seconds <= 0)
            return null;
        // Guard the ceiling so a nonsense provider value cannot overflow TimeSpan.
        if (seconds > TimeSpan.MaxValue.TotalSeconds)
            return null;
        return TimeSpan.FromSeconds(seconds);
    }
}
