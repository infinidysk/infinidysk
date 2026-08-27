using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Services.Benchmark;

/// <summary>
/// Measures real download speed and latency against a single provider and
/// recommends the smallest transfer connection count that nearly maxes out throughput
/// (the diminishing-returns "knee"), plus whether NNTP pipelining helps and at
/// what depth.
///
/// Safety model — the test never disrupts normal operation or usage accounting:
///   • It opens its own ad-hoc connections via <see cref="UsenetStreamingClient.CreateNewConnection"/>,
///     bypassing the shared connection pool, byte tracker and metrics writer.
///   • It probes a few steps above the configured max but stops the instant the
///     provider refuses another connection (the classic 502 "too many connections"),
///     treating that as the real ceiling.
///   • Every level is byte- and time-bounded, and the whole run honours the
///     caller's cancellation token (closing the modal aborts it cleanly).
/// </summary>
public sealed class UsenetBenchmarkService(WebsocketManager websocketManager, BenchmarkCorpusProvider corpus)
{
    // Never open more than this many sockets at once, regardless of provider/config.
    private const int HardConnectionCeiling = 50;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Publishes a terminal websocket frame when the run fails before a
    /// <see cref="BenchmarkResult"/> exists (connect/login errors, cancel, etc.).
    /// </summary>
    public void ReportFailure(string error)
    {
        var update = new BenchmarkProgressUpdate
        {
            Phase = "done",
            Status = error,
            Percent = 100,
            DataUsedBytes = 0,
            DataBudgetBytes = 0,
            Sweep = [],
            Error = error,
        };
        _ = websocketManager.SendMessage(WebsocketTopic.BenchmarkProgress, JsonSerializer.Serialize(update, JsonOptions));
    }

    public async Task<BenchmarkResult> RunAsync(
        UsenetProviderConfig.ConnectionDetails provider,
        int configuredMaxConnections,
        BenchmarkIntensity intensity,
        bool pipeliningOnly,
        long? dataBudgetBytes,
        int? verifyConnections,
        CancellationToken ct)
    {
        var profile = BenchmarkProfile.For(intensity);
        var budget = Math.Max(50_000_000, dataBudgetBytes ?? profile.HardTotalBytes);
        var result = new BenchmarkResult { PipeliningOnly = pipeliningOnly, DataBudgetBytes = budget };
        var runClock = Stopwatch.StartNew();
        long Remaining() => Math.Max(0, budget - result.DataUsedBytes);
        // Hold back enough for baseline + each pipelining depth so a full auto-tune
        // still produces a depth recommendation instead of burning the whole budget
        // on the connection sweep.
        long SweepRemaining(double megaBytesPerSec) =>
            Math.Max(0, Remaining() - PipeliningReserveBytes(profile, megaBytesPerSec, budget));
        void StampElapsed() => result.ElapsedSeconds = Math.Round(runClock.Elapsed.TotalSeconds, 1);

        using var ladder = new BenchmarkConnectionLadder(provider);

        // 1) Latency — also doubles as a connectivity/credentials check.
        Report("latency", "Measuring latency…", 5, result, null);
        await ladder.EnsureAsync(1, ct).ConfigureAwait(false);
        result.Latency = await MeasureLatencyAsync(
            ladder.Connections[0], profile.LatencySamples, ct).ConfigureAwait(false);

        // 2) Corpus — real message-ids to download.
        Report("corpus", "Gathering test articles…", 12, result, null);
        var ids = await corpus.GetSegmentPoolAsync(profile.MaxCorpusSegments, ct).ConfigureAwait(false);
        if (ids.Count == 0)
        {
            result.ThroughputTested = false;
            result.Warnings.Add(
                "No downloaded articles were available to measure speed, so only latency was tested. " +
                "Download something first, then re-run to get a transfer-connection recommendation.");
            StampElapsed();
            Report("done", "Done — latency only.", 100, result, null, includeResult: true);
            return result;
        }

        var pool = new BenchmarkSegmentPool(ids);
        result.ThroughputTested = true;
        if (ids.Count < 200)
            result.Warnings.Add("Only a small pool of test articles was available, so speed numbers may be a little noisy.");

        // Pipelining-only mode: leave the connection count alone and just find
        // the best pipelining depth at the count the user already runs.
        if (pipeliningOnly)
        {
            var conns = Math.Clamp(configuredMaxConnections, 1, HardConnectionCeiling);
            Report("pipelining", $"Testing pipelining at {conns} connection{(conns == 1 ? "" : "s")}…", 30, result, conns);
            await ladder.EnsureAsync(conns, ct).ConfigureAwait(false);
            result.Pipelining = await MeasurePipeliningAsync(
                ladder, pool, profile, FocusedPipelineDepths(intensity), Remaining,
                bytes => result.DataUsedBytes += bytes, result.Warnings, ct).ConfigureAwait(false);

            if (conns >= 24)
                result.Warnings.Add(
                    "At high connection counts, pipelining usually adds little — running many connections in " +
                    "parallel already hides most of the per-request latency it would otherwise save.");
        }
        else if (verifyConnections is int vc0)
        {
            var vc = Math.Clamp(vc0, 1, HardConnectionCeiling);
            Report("sweep", $"Verifying {vc} connection{(vc == 1 ? "" : "s")}…", 40, result, vc);
            var have = await ladder.EnsureAsync(vc, ct).ConfigureAwait(false);

            // Size the target for this concurrency from the start. A fixed PerLevelBytes
            // bootstrap (e.g. 20 MB) finishes during warmup on fast lines at high
            // connection counts, leaving a 0 MB/s measure window.
            var sample = await MeasureThroughputAsync(
                ladder, pool, AdaptiveTargetBytes(0, profile, Remaining(), have),
                profile.WarmupDuration, profile.MeasureWindow, profile.PerLevelMaxDuration,
                pipeliningDepth: 0, ct).ConfigureAwait(false);
            result.DataUsedBytes += sample.Bytes;

            if (sample.ExhaustedDuringWarmup
                && sample.MegaBytesPerSec > 0
                && Remaining() > MinUsefulBytes(sample.MegaBytesPerSec, profile))
            {
                Report("sweep", $"Re-measuring {have} connection{(have == 1 ? "" : "s")}…", 65, result, have);
                var retry = await MeasureThroughputAsync(
                    ladder, pool, AdaptiveTargetBytes(sample.MegaBytesPerSec, profile, Remaining(), have),
                    profile.WarmupDuration, profile.MeasureWindow, profile.PerLevelMaxDuration,
                    pipeliningDepth: 0, ct).ConfigureAwait(false);
                result.DataUsedBytes += retry.Bytes;
                if (retry.MegaBytesPerSec > 0)
                    sample = retry;
            }

            result.Sweep.Add(new BenchmarkSweepPoint
            {
                Connections = have,
                MegaBytesPerSec = Math.Round(sample.MegaBytesPerSec, 2),
                Cv = Math.Round(sample.Cv, 3),
            });
            result.RecommendedConnections = have > 0 ? have : vc;
            result.VerificationRun = true;
            if (have == 0)
                result.Warnings.Add("Could not open any connections for the verification run.");
            else if (have < vc)
                result.Warnings.Add($"Only {have} of {vc} connections could be opened for the verification run.");
            if (sample.MegaBytesPerSec < 0.5)
                result.Warnings.Add(
                    "Verification measured almost no throughput. The line may have been busy, or the test " +
                    "article pool was exhausted — try again when idle, or re-run a full speed test.");
        }
        else
        {
            // 3) Throughput sweep — climb connection counts until the knee or the cap.
            var levels = BuildLevels(configuredMaxConnections, profile);
            int? providerCap = null;
            double lastMegaBytesPerSec = 0;
            for (var i = 0; i < levels.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var level = levels[i];

                if (SweepRemaining(lastMegaBytesPerSec) < MinUsefulBytes(lastMegaBytesPerSec, profile))
                {
                    result.BudgetLimited = true;
                    result.Warnings.Add(
                        "Reached the data budget before testing every connection level. Raise the budget for a fuller picture.");
                    break;
                }

                Report("sweep", $"Testing {level} connection{(level == 1 ? "" : "s")}…",
                    ProgressPercent(15, 75, i, levels.Count), result, level);

                var have = await ladder.EnsureAsync(level, ct).ConfigureAwait(false);
                if (have == 0)
                {
                    providerCap ??= Math.Max(1, i > 0 ? levels[i - 1] : 1);
                    break;
                }

                var sample = await MeasureThroughputAsync(
                    ladder, pool, AdaptiveTargetBytes(lastMegaBytesPerSec, profile, SweepRemaining(lastMegaBytesPerSec), have),
                    profile.WarmupDuration, profile.MeasureWindow, profile.PerLevelMaxDuration,
                    pipeliningDepth: 0, ct).ConfigureAwait(false);
                result.DataUsedBytes += sample.Bytes;

                // Tiny bootstrap target finished in warmup → rate recovered but noisy.
                // Retry once with a properly sized budget before recording the point.
                if (sample.ExhaustedDuringWarmup
                    && sample.MegaBytesPerSec > 0
                    && SweepRemaining(sample.MegaBytesPerSec) > MinUsefulBytes(sample.MegaBytesPerSec, profile))
                {
                    lastMegaBytesPerSec = Math.Max(lastMegaBytesPerSec, sample.MegaBytesPerSec);
                    Report("sweep", $"Re-measuring {have} connection{(have == 1 ? "" : "s")}…",
                        ProgressPercent(15, 75, i, levels.Count), result, have);
                    var retry = await MeasureThroughputAsync(
                        ladder, pool, AdaptiveTargetBytes(lastMegaBytesPerSec, profile, SweepRemaining(lastMegaBytesPerSec), have),
                        profile.WarmupDuration, profile.MeasureWindow, profile.PerLevelMaxDuration,
                        pipeliningDepth: 0, ct).ConfigureAwait(false);
                    result.DataUsedBytes += retry.Bytes;
                    if (retry.MegaBytesPerSec > 0)
                        sample = retry;
                }

                lastMegaBytesPerSec = Math.Max(lastMegaBytesPerSec, sample.MegaBytesPerSec);

                result.Sweep.Add(new BenchmarkSweepPoint
                {
                    Connections = have,
                    MegaBytesPerSec = Math.Round(sample.MegaBytesPerSec, 2),
                    Cv = Math.Round(sample.Cv, 3),
                });
                Report("sweep", $"{have} conn → {sample.MegaBytesPerSec:0.0} MB/s",
                    ProgressPercent(15, 75, i + 1, levels.Count), result, have);

                if (have < level)
                {
                    providerCap = have;
                    result.Warnings.Add(
                        $"Your provider wouldn't allow more than {have} connections at once, so the test stopped there.");
                    break;
                }
            }

            result.ProviderConnectionCap = providerCap;
            result.RecommendedConnections = CapTransferRecommendation(
                DetectKnee(result.Sweep, providerCap, result.Warnings, out var stillClimbing),
                configuredMaxConnections,
                result.Warnings);
            result.StillClimbing = stillClimbing;

            // Thorough only: confirm the pick with a second independent window and blend.
            // Confirm may spend into the pipelining reserve if needed for accuracy.
            if (intensity == BenchmarkIntensity.Thorough
                && result.RecommendedConnections is int knee
                && Remaining() > MinUsefulBytes(lastMegaBytesPerSec, profile))
            {
                Report("sweep", $"Confirming {knee} connection{(knee == 1 ? "" : "s")}…", 80, result, knee);
                ladder.ShrinkTo(knee);
                if (ladder.Count > 0)
                {
                    var confirm = await MeasureThroughputAsync(
                        ladder, pool, AdaptiveTargetBytes(lastMegaBytesPerSec, profile, Remaining(), knee),
                        profile.WarmupDuration, profile.MeasureWindow, profile.PerLevelMaxDuration,
                        pipeliningDepth: 0, ct).ConfigureAwait(false);
                    result.DataUsedBytes += confirm.Bytes;
                    var point = result.Sweep.FirstOrDefault(p => p.Connections == knee);
                    if (point != null && confirm.MegaBytesPerSec > 0)
                    {
                        if (point.MegaBytesPerSec > 0)
                        {
                            result.ConfirmDeltaPct = Math.Round(
                                Math.Abs(confirm.MegaBytesPerSec - point.MegaBytesPerSec) / point.MegaBytesPerSec * 100, 1);
                            if (result.ConfirmDeltaPct > 20)
                                result.Warnings.Add(
                                    "The confirmation run didn't reproduce the measured speed closely, " +
                                    "so the recommendation may shift slightly between runs.");
                        }

                        point.MegaBytesPerSec = Math.Round((point.MegaBytesPerSec + confirm.MegaBytesPerSec) / 2, 2);
                        point.Cv = Math.Round(Math.Max(point.Cv, confirm.Cv), 3);
                        result.RecommendedConnections = CapTransferRecommendation(
                            DetectKnee(result.Sweep, providerCap, [], out stillClimbing),
                            configuredMaxConnections,
                            result.Warnings);
                        result.StillClimbing = stillClimbing;
                    }
                }
            }

            // 4) Pipelining — compare off vs. a few depths at a moderate concurrency.
            if (result.Sweep.Count > 0 && Remaining() > MinUsefulBytes(lastMegaBytesPerSec, profile))
            {
                var pipeConns = Math.Min(result.RecommendedConnections ?? 1, profile.PipelineTestConnections);
                if (providerCap.HasValue) pipeConns = Math.Min(pipeConns, providerCap.Value);
                pipeConns = Math.Max(1, pipeConns);

                Report("pipelining", "Testing NNTP pipelining…", 88, result, pipeConns);
                ladder.ShrinkTo(pipeConns);
                await ladder.EnsureAsync(pipeConns, ct).ConfigureAwait(false);
                result.Pipelining = await MeasurePipeliningAsync(
                    ladder, pool, profile, profile.PipelineDepths, Remaining,
                    bytes => result.DataUsedBytes += bytes, result.Warnings, ct).ConfigureAwait(false);
            }
            else if (result.Sweep.Count > 0)
            {
                result.Warnings.Add(
                    "Skipped the pipelining test — not enough data budget left after the connection sweep. " +
                    "Raise the budget or run \"Only tune pipelining\" separately.");
            }
        }

        if (pool.WrappedAround)
        {
            result.WrappedPool = true;
            result.Warnings.Add(
                "The test re-downloaded some articles more than once, so provider caching may make speeds read high. " +
                "A larger library of completed downloads gives the test more unique data.");
        }
        if (pool.DeadCount > 0 && pool.DeadCount * 10 > pool.Count)
            result.Warnings.Add(
                $"{pool.DeadCount} test articles were no longer available on the provider, which can bias speeds low. " +
                "Downloading something recent refreshes the test pool.");

        result.Confidence = ComputeConfidence(result);
        StampElapsed();
        Report("done", "Done.", 100, result, null, includeResult: true);
        return result;
    }

    // ---- Phases ----------------------------------------------------------

    private static async Task<BenchmarkLatency> MeasureLatencyAsync(
        INntpClient conn, int samples, CancellationToken ct)
    {
        await conn.DateAsync(ct).ConfigureAwait(false); // warm-up; excludes TLS/first-command setup
        var measured = new List<double>(samples);
        for (var i = 0; i < samples; i++)
        {
            ct.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            await conn.DateAsync(ct).ConfigureAwait(false);
            sw.Stop();
            measured.Add(sw.Elapsed.TotalMilliseconds);
        }

        return new BenchmarkLatency
        {
            MinMs = Math.Round(measured.Min(), 1),
            AvgMs = Math.Round(measured.Average(), 1),
            Samples = measured.Count,
        };
    }

    private async Task<BenchmarkPipelining> MeasurePipeliningAsync(
        BenchmarkConnectionLadder ladder, BenchmarkSegmentPool pool, BenchmarkProfile profile,
        int[] depths, Func<long> remainingBudget, Action<long> addData, List<string> warnings,
        CancellationToken ct)
    {
        var result = new BenchmarkPipelining { TestedAtConnections = ladder.Count };
        double last = 0;

        // Baseline: pipelining off.
        var baseline = await MeasureThroughputAsync(
            ladder, pool, AdaptiveTargetBytes(last, profile, remainingBudget(), ladder.Count),
            profile.WarmupDuration, profile.MeasureWindow, profile.PerLevelMaxDuration,
            pipeliningDepth: 0, ct).ConfigureAwait(false);
        addData(baseline.Bytes);
        last = baseline.MegaBytesPerSec;
        result.BaselineMegaBytesPerSec = Math.Round(baseline.MegaBytesPerSec, 2);
        if (baseline.OpenedConnections == 0) return result;

        var bestMegaBytesPerSec = baseline.MegaBytesPerSec;
        var bestDepth = 0;
        foreach (var depth in depths)
        {
            ct.ThrowIfCancellationRequested();
            if (remainingBudget() < MinUsefulBytes(last, profile))
            {
                warnings.Add("Reached the data budget before testing every pipelining depth.");
                break;
            }

            var sample = await MeasureThroughputAsync(
                ladder, pool, AdaptiveTargetBytes(last, profile, remainingBudget(), ladder.Count),
                profile.WarmupDuration, profile.MeasureWindow, profile.PerLevelMaxDuration,
                depth, ct).ConfigureAwait(false);
            addData(sample.Bytes);
            last = Math.Max(last, sample.MegaBytesPerSec);
            result.Tested.Add(new BenchmarkPipeliningPoint { Depth = depth, MegaBytesPerSec = Math.Round(sample.MegaBytesPerSec, 2) });
            if (sample.MegaBytesPerSec > bestMegaBytesPerSec) { bestMegaBytesPerSec = sample.MegaBytesPerSec; bestDepth = depth; }
        }

        // Only recommend turning it on if it's a clear (>10%) win over the baseline.
        if (bestDepth > 0 && baseline.MegaBytesPerSec > 0 && bestMegaBytesPerSec >= baseline.MegaBytesPerSec * 1.10)
        {
            result.RecommendEnabled = true;
            result.RecommendedDepth = bestDepth;
        }
        else
        {
            result.RecommendEnabled = false;
            result.RecommendedDepth = bestDepth > 0 ? bestDepth : 8;
        }

        return result;
    }

    // ---- Throughput core -------------------------------------------------

    private readonly record struct ThroughputSample(
        double MegaBytesPerSec,
        double Cv,
        long Bytes,
        int OpenedConnections,
        double WindowSeconds,
        long WindowBytes,
        bool ExhaustedDuringWarmup);

    private static async Task<ThroughputSample> MeasureThroughputAsync(
        BenchmarkConnectionLadder ladder, BenchmarkSegmentPool pool, long targetBytes,
        TimeSpan warmup, TimeSpan window, TimeSpan maxDuration, int pipeliningDepth,
        CancellationToken ct)
    {
        var opened = ladder.Count;
        if (opened == 0) return new ThroughputSample(0, 0, 0, 0, 0, 0, false);

        var counter = new StrongBox<long>(0);
        var softStop = new StrongBox<int>(0);
        var dead = new System.Collections.Concurrent.ConcurrentBag<INntpClient>();
        // Hard cancel is only a safety net (maxDuration / caller abort). Ending the
        // measure window uses soft-stop so we finish the current BODY cleanly —
        // cancelling mid-article poisons the socket (drain-limit / desync).
        using var hardCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        hardCts.CancelAfter(maxDuration);
        var hardToken = hardCts.Token;

        var workers = ladder.Connections
            .Select(conn => Task.Run(async () =>
            {
                var healthy = await DownloadWorkerAsync(
                    conn, pool, targetBytes, counter, softStop, pipeliningDepth, hardToken)
                    .ConfigureAwait(false);
                if (!healthy) dead.Add(conn);
            }))
            .ToList();

        // Warm-up: let TCP windows open and the first-article latency pass, unmeasured.
        await SafeDelay(warmup, hardToken).ConfigureAwait(false);
        var startBytes = Interlocked.Read(ref counter.Value);
        var sw = Stopwatch.StartNew();

        // Steady window: snapshot the counter into ~500ms buckets with real timestamps.
        var buckets = new List<(long Bytes, double Seconds)>();
        var prevBytes = startBytes;
        var prevTime = 0.0;
        while (sw.Elapsed < window && !hardToken.IsCancellationRequested && !workers.All(w => w.IsCompleted))
        {
            await SafeDelay(TimeSpan.FromMilliseconds(500), hardToken).ConfigureAwait(false);
            var nowBytes = Interlocked.Read(ref counter.Value);
            var nowTime = sw.Elapsed.TotalSeconds;
            buckets.Add((nowBytes - prevBytes, nowTime - prevTime));
            (prevBytes, prevTime) = (nowBytes, nowTime);
        }

        var endBytes = Interlocked.Read(ref counter.Value);
        var elapsed = Math.Max(sw.Elapsed.TotalSeconds, 0.001);

        // Soft-stop: stop starting new articles; let in-flight BODY drains finish.
        Interlocked.Exchange(ref softStop.Value, 1);
        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Workers stop via cancellation during soft-stop.
        }

        ladder.Prune(dead.ToArray());

        var totalBytes = Interlocked.Read(ref counter.Value);
        var windowBytes = Math.Max(0, endBytes - startBytes);
        var (steady, cv) = ComputeSteadyRate(buckets, windowBytes, elapsed);

        // Byte budget finished during warmup → measure window is idle/empty and the
        // median rate is 0 even though plenty of data moved. Recover an overall rate
        // so the next level can size AdaptiveTargetBytes correctly.
        var wallSeconds = Math.Max(0.05, warmup.TotalSeconds + elapsed);
        var exhaustedDuringWarmup = totalBytes >= Math.Max(1_000_000, targetBytes * 3 / 4)
            && windowBytes < Math.Max(500_000, totalBytes / 10);
        if ((steady < 0.05 || exhaustedDuringWarmup) && totalBytes > 1_000_000)
        {
            steady = totalBytes / wallSeconds / 1_000_000.0;
            cv = Math.Max(cv, 0.5);
        }

        return new ThroughputSample(steady, cv, totalBytes, opened, elapsed, windowBytes, exhaustedDuringWarmup);
    }

    /// <returns>
    /// True when the connection is still reusable. False when it saw a hard cancel
    /// or protocol failure and must be disposed (NNTP mid-BODY abort poisons the socket).
    /// </returns>
    private static async Task<bool> DownloadWorkerAsync(
        INntpClient conn, BenchmarkSegmentPool pool, long targetBytes,
        StrongBox<long> counter, StrongBox<int> softStop, int depth, CancellationToken hardCt)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            if (depth <= 1)
            {
                while (Volatile.Read(ref softStop.Value) == 0
                       && !hardCt.IsCancellationRequested
                       && Interlocked.Read(ref counter.Value) < targetBytes)
                {
                    var id = pool.Next();
                    try
                    {
                        var response = await conn.DecodedBodyAsync(id, hardCt).ConfigureAwait(false);
                        // Drain with hardCt only — soft-stop never cancels mid-BODY.
                        await DrainAsync(response.Stream!, buffer, counter, hardCt).ConfigureAwait(false);
                    }
                    catch (UsenetArticleNotFoundException)
                    {
                        pool.MarkDead(id);
                    }
                }
            }
            else
            {
                while (Volatile.Read(ref softStop.Value) == 0
                       && !hardCt.IsCancellationRequested
                       && Interlocked.Read(ref counter.Value) < targetBytes)
                {
                    var batch = pool.NextBatch(depth * 4);
                    // Finish the whole batch once started so pipelined responses stay in sync.
                    await foreach (var r in conn.DecodedBodiesPipelinedAsync(batch, depth, hardCt)
                                       .WithCancellation(hardCt).ConfigureAwait(false))
                    {
                        if (r is { Found: true, Stream: not null })
                            await DrainAsync(r.Stream, buffer, counter, hardCt).ConfigureAwait(false);
                        if (hardCt.IsCancellationRequested)
                            break;
                    }
                }
            }

            // Hard cancel mid-BODY leaves the connection unusable for the next level.
            return !hardCt.IsCancellationRequested;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
#pragma warning disable CA2016 // CA2016: classify cancellation regardless of the ambient token -- forwarding it would misclassify cancellations from internal timeout/child tokens
        catch (Exception e) when (!e.IsCancellationException() && e is not OutOfMemoryException)
#pragma warning restore CA2016
        {
            Log.Debug(e, "Benchmark download worker stopped early.");
            return false;
        }
    }

    private static async Task DrainAsync(Stream stream, byte[] buffer, StrongBox<long> counter, CancellationToken ct)
    {
        await using (stream)
        {
            int n;
            while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                Interlocked.Add(ref counter.Value, n);
        }
    }

    // ---- Helpers ---------------------------------------------------------

    // Bytes needed to keep the pipe busy through warm-up plus a minimally-useful (~1.5s) window.
    internal static long MinUsefulBytes(double lastMegaBytesPerSec, BenchmarkProfile profile)
    {
        var seconds = profile.WarmupDuration.TotalSeconds + 1.5;
        var bytesPerSec = lastMegaBytesPerSec > 0 ? lastMegaBytesPerSec * 1_000_000 : 2_000_000;
        return Math.Max(4_000_000, (long)(bytesPerSec * seconds));
    }

    // Hold back enough for a pipelining baseline + each tested depth. Caps at 25% of
    // the total budget so small Auto runs still spend most of their quota on the sweep.
    internal static long PipeliningReserveBytes(
        BenchmarkProfile profile, double lastMegaBytesPerSec, long totalBudget)
    {
        var steps = 1 + profile.PipelineDepths.Length;
        var perStep = MinUsefulBytes(Math.Max(lastMegaBytesPerSec, 10), profile);
        var reserve = perStep * steps;
        var cap = Math.Max(0, totalBudget / 4);
        return Math.Min(reserve, cap);
    }

    // Target enough bytes that workers never run dry before the wall clock stops the
    // window, even if this level doubles the previous level's throughput. When we
    // have no speed estimate yet, scale the floor with connection count so parallel
    // workers can't burn a tiny budget during warmup alone.
    internal static long AdaptiveTargetBytes(
        double lastMegaBytesPerSec, BenchmarkProfile profile, long remainingBudget, int connections = 1)
    {
        var seconds = (profile.WarmupDuration + profile.MeasureWindow).TotalSeconds;
        long est;
        if (lastMegaBytesPerSec > 0)
        {
            est = (long)(lastMegaBytesPerSec * 1_000_000 * 2.0 * seconds);
        }
        else
        {
            // Assume at least ~2 MB/s per connection until we have a real sample.
            var bootstrap = (long)(Math.Max(1, connections) * 2_000_000.0 * 2.0 * seconds);
            est = Math.Max(profile.PerLevelBytes, bootstrap);
        }

        var max = Math.Max(1_000_000, remainingBudget);
        var min = Math.Min(profile.PerLevelBytes, max);
        return Math.Clamp(est, min, max);
    }

    // Median-of-positive-buckets rate + coefficient of variation. Empty (gap) buckets
    // are ignored so ARTICLE RTT holes don't force a median of 0. Falls back to the
    // whole-window mean when there aren't enough positive buckets, or when the median
    // is ~0 despite bytes moving in the window.
    internal static (double MegaBytesPerSec, double Cv) ComputeSteadyRate(
        IReadOnlyList<(long Bytes, double Seconds)> buckets,
        long fallbackBytes,
        double fallbackSeconds)
    {
        var windowMean = fallbackSeconds > 0.05
            ? fallbackBytes / fallbackSeconds / 1_000_000.0
            : 0;

        var rates = buckets
            .Where(b => b.Seconds > 0.05 && b.Bytes > 0)
            .Select(b => b.Bytes / b.Seconds / 1_000_000.0)
            .ToList();

        if (rates.Count < 3)
        {
            return (windowMean, rates.Count == 0 ? 1.0 : 0.5);
        }

        var sorted = rates.OrderBy(r => r).ToList();
        var median = sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;

        if (median < 0.05 && windowMean > median)
            return (windowMean, 0.5);

        var avg = rates.Average();
        var cv = avg > 0
            ? Math.Sqrt(rates.Sum(r => (r - avg) * (r - avg)) / rates.Count) / avg
            : 0;
        return (median, cv);
    }

    internal static int? CapTransferRecommendation(
        int? recommendation,
        int configuredProviderLimit,
        List<string>? warnings = null)
    {
        if (recommendation is not { } value) return null;

        var providerLimit = Math.Max(1, configuredProviderLimit);
        if (value <= providerLimit) return value;

        const string warningPrefix = "The transfer recommendation was limited to your configured Provider Connection Limit";
        var warning = $"{warningPrefix} ({providerLimit}). Increase that limit and re-run Auto-tune " +
                      "if you want to test a higher transfer allocation.";
        if (warnings is not null
            && !warnings.Any(existing => existing.StartsWith(warningPrefix, StringComparison.Ordinal)))
        {
            warnings.Add(warning);
        }

        return providerLimit;
    }

    internal static string ComputeConfidence(BenchmarkResult result)
    {
        if (!result.ThroughputTested) return "low";

        var maxCv = KneeRegionMaxCv(result);

        // Budget exhaustion only tanks confidence when the curve was still climbing —
        // i.e. the untested upper levels actually mattered.
        if (result.BudgetLimited && result.StillClimbing) return "low";
        if (maxCv > 0.30) return "low";

        // A tight confirm-run agreement can override the wrap-pool medium cap.
        var confirmTight = result.ConfirmDeltaPct is <= 5;
        var confirmLoose = result.ConfirmDeltaPct is > 20;

        var confidence = (!confirmTight && result.WrappedPool) || maxCv > 0.15
            ? "medium"
            : "high";

        // Budget ran out after a found plateau: downgrade high → medium at most.
        if (result.BudgetLimited && confidence == "high")
            confidence = "medium";

        // Confirm window disagreed with the original knee measurement.
        if (confirmLoose)
            confidence = confidence == "high" ? "medium" : "low";

        return confidence;
    }

    // CV near the recommended level (or the upper half of the sweep) matters;
    // jitter at 1–2 connections shouldn't poison an otherwise clean run.
    internal static double KneeRegionMaxCv(BenchmarkResult result)
    {
        if (result.Sweep.Count == 0) return 0;
        var threshold = result.RecommendedConnections is int rec and > 0
            ? Math.Max(1, rec / 2)
            : result.Sweep.Max(p => p.Connections) / 2;
        var region = result.Sweep.Where(p => p.Connections >= threshold).ToList();
        if (region.Count == 0) region = result.Sweep;
        return region.Max(p => p.Cv);
    }

    private static List<int> BuildLevels(int configuredMaxConnections, BenchmarkProfile profile)
    {
        // Probe a few steps above the configured max to discover the real sweet
        // spot, but never beyond a safe hard ceiling.
        var ceiling = Math.Clamp(
            Math.Max(configuredMaxConnections + 10, configuredMaxConnections * 2),
            8, HardConnectionCeiling);

        return profile.SweepLevels
            .Where(l => l > 0 && l <= ceiling)
            .Distinct()
            .OrderBy(l => l)
            .ToList();
    }

    internal static int? DetectKnee(
        List<BenchmarkSweepPoint> sweep, int? providerCap, List<string> warnings)
        => DetectKnee(sweep, providerCap, warnings, out _);

    internal static int? DetectKnee(
        List<BenchmarkSweepPoint> sweep, int? providerCap, List<string> warnings,
        out bool stillClimbing)
    {
        stillClimbing = false;
        if (sweep.Count == 0) return null;
        var ordered = sweep.OrderBy(p => p.Connections).ToList();

        // A mostly-zero sweep (poisoned sockets / failed measure windows) must not
        // recommend the lone spike at the connection ceiling.
        const double minMeaningfulMegaBytesPerSec = 0.5;
        var topTwo = ordered.OrderByDescending(p => p.MegaBytesPerSec).Take(2).ToList();
        if (topTwo.Count < 2
            || topTwo[0].MegaBytesPerSec <= minMeaningfulMegaBytesPerSec
            || topTwo[1].MegaBytesPerSec <= minMeaningfulMegaBytesPerSec
            || topTwo[1].MegaBytesPerSec < topTwo[0].MegaBytesPerSec * 0.10)
        {
            warnings.Add(
                "The speed test couldn't get steady throughput at enough connection levels " +
                "to make a recommendation. Re-run when idle.");
            return null;
        }

        // Reference peak = mean of the two best points so a single lucky spike can't
        // drag the recommendation around between runs.
        var peakRef = (topTwo[0].MegaBytesPerSec + topTwo[1].MegaBytesPerSec) / 2.0;
        if (peakRef <= 0) return null;

        var knee = ordered.First(p => p.MegaBytesPerSec >= 0.92 * peakRef).Connections;
        if (providerCap.HasValue) knee = Math.Min(knee, providerCap.Value);

        var best = ordered.Max(p => p.MegaBytesPerSec);
        var peak = ordered[^1];
        if (peak.MegaBytesPerSec >= best - 1e-9 && ordered.Count >= 2)
        {
            var prev = ordered[^2];
            if (prev.MegaBytesPerSec > 0 && (peak.MegaBytesPerSec - prev.MegaBytesPerSec) / prev.MegaBytesPerSec > 0.08)
            {
                stillClimbing = true;
                warnings.Add("Speed was still climbing at the highest level tested — a faster line or even more connections may help.");
            }
        }

        if (ordered.Any(p => p.Cv > 0.25))
            warnings.Add(
                "Some measurements were noisy (throughput fluctuated during the window), " +
                "so the recommendation may shift slightly between runs.");

        return Math.Max(1, knee);
    }

    // A wider depth spread for pipelining-only runs, where the depth is the whole point.
    private static int[] FocusedPipelineDepths(BenchmarkIntensity intensity) =>
        intensity == BenchmarkIntensity.Thorough ? [4, 8, 16, 32] : [4, 8, 16];

    private static async Task SafeDelay(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation ends the delay early; callers re-check the token.
        }
    }

    private static int ProgressPercent(int start, int end, int step, int totalSteps) =>
        start + (int)((end - start) * (double)step / Math.Max(1, totalSteps));

    private void Report(
        string phase,
        string status,
        int percent,
        BenchmarkResult result,
        int? currentConnections,
        string? error = null,
        bool includeResult = false)
    {
        var update = new BenchmarkProgressUpdate
        {
            Phase = phase,
            Status = status,
            Percent = percent,
            CurrentConnections = currentConnections,
            DataUsedBytes = result.DataUsedBytes,
            DataBudgetBytes = result.DataBudgetBytes,
            Sweep = result.Sweep.Select(p => new BenchmarkSweepPoint
            {
                Connections = p.Connections,
                MegaBytesPerSec = p.MegaBytesPerSec,
                Cv = p.Cv,
            }).ToList(),
            Result = includeResult ? result : null,
            Error = error,
        };
        // Fire-and-forget: progress is best-effort and must not block the run.
        _ = websocketManager.SendMessage(WebsocketTopic.BenchmarkProgress, JsonSerializer.Serialize(update, JsonOptions));
    }
}
