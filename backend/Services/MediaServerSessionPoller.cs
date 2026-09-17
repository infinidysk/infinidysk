using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Clients.MediaServers;
using NzbWebDAV.Config;
using NzbWebDAV.Models.Playback;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Reconciles the small current-session list from configured media servers.
/// A successful response is authoritative. A failed request retains the last
/// confirmed rows as stale for a bounded grace period.
/// </summary>
public sealed class MediaServerSessionPoller : BackgroundService
{
    internal static readonly TimeSpan NormalPollInterval = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(4);
    internal static readonly TimeSpan StaleGrace = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private readonly ConfigManager _configManager;
    private readonly PlaybackSessionRegistry _registry;
    private readonly PlaybackDavItemResolver _resolver;
    private readonly Dictionary<MediaServerType, IMediaPlaybackSessionSource> _sources;
    private readonly Func<double> _jitter;
    private readonly Dictionary<Guid, PollSchedule> _schedules = new();
    private readonly SemaphoreSlim _tickGate = new(1, 1);

    public MediaServerSessionPoller(
        ConfigManager configManager,
        PlaybackSessionRegistry registry,
        PlaybackDavItemResolver resolver,
        IEnumerable<IMediaPlaybackSessionSource> sources)
        : this(
            configManager,
            registry,
            resolver,
            sources,
            () => RandomNumberGenerator.GetInt32(0, 1_000_001) / 1_000_000d)
    {
    }

    internal MediaServerSessionPoller(
        ConfigManager configManager,
        PlaybackSessionRegistry registry,
        PlaybackDavItemResolver resolver,
        IEnumerable<IMediaPlaybackSessionSource> sources,
        Func<double> jitter)
    {
        _configManager = configManager;
        _registry = registry;
        _resolver = resolver;
        _sources = sources.ToDictionary(source => source.ServerType);
        _jitter = jitter;
    }

    public override void Dispose()
    {
        _tickGate.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollDueAsync(DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false);
                await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Log.Warning(exception, "Media-server session reconciliation tick failed");
                await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    internal async Task PollDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await _tickGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var enabled = _configManager.GetMediaServerConfig().GetEnabledInstances().ToList();
            var enabledIds = enabled.Select(instance => instance.Id).ToHashSet();

            foreach (var id in _schedules.Keys.Where(id => !enabledIds.Contains(id)).ToList())
            {
                _schedules.Remove(id);
                _registry.RemoveInstance(id);
            }

            var due = new List<(MediaServerInstance Instance, PollSchedule Schedule)>();
            foreach (var instance in enabled)
            {
                if (!_schedules.TryGetValue(instance.Id, out var schedule))
                {
                    schedule = new PollSchedule(instance);
                    _schedules.Add(instance.Id, schedule);
                }
                else if (!schedule.Instance.RuntimeEquivalent(instance))
                {
                    _registry.RemoveInstance(instance.Id);
                    schedule = new PollSchedule(instance);
                    _schedules[instance.Id] = schedule;
                }
                else
                {
                    schedule.Instance = instance;
                }

                if (schedule.NextPollAt <= now)
                    due.Add((instance, schedule));
            }

            await Task.WhenAll(
                due.Select(entry => PollInstanceAsync(
                    entry.Instance, entry.Schedule, now, cancellationToken))).ConfigureAwait(false);

            _registry.ExpireStaleExternal(now, StaleGrace);
        }
        finally
        {
            _tickGate.Release();
        }
    }

    private async Task PollInstanceAsync(
        MediaServerInstance instance,
        PollSchedule schedule,
        DateTimeOffset now,
        CancellationToken stoppingToken)
    {
        if (!_sources.TryGetValue(instance.Type, out var source))
        {
            MarkFailure(instance, schedule, now, "unsupported_source");
            return;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(RequestTimeout);

            var observations = await source.GetSessionsAsync(instance, timeout.Token).ConfigureAwait(false);
            var mapped = new List<MappedPlaybackObservation>();
            foreach (var observation in observations)
            {
                var davItemId = _resolver.Resolve(instance, observation);
                if (davItemId is { } exactId)
                    mapped.Add(new MappedPlaybackObservation(observation, exactId));
            }

            _registry.ReconcileExternal(instance, mapped, now);
            schedule.Failures = 0;
            schedule.NextPollAt = now + NormalPollInterval;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            MarkFailure(instance, schedule, now, "timeout");
        }
        catch (HttpRequestException)
        {
            MarkFailure(instance, schedule, now, "http");
        }
        catch (JsonException)
        {
            MarkFailure(instance, schedule, now, "json");
        }
        catch (InvalidDataException)
        {
            MarkFailure(instance, schedule, now, "invalid_data");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.Debug(
                exception,
                "Media-server session poll failed for {ServerType} instance {InstanceName}",
                instance.Type,
                instance.Name);
            MarkFailure(instance, schedule, now, exception.GetType().Name);
        }
    }

    private void MarkFailure(
        MediaServerInstance instance,
        PollSchedule schedule,
        DateTimeOffset now,
        string errorKind)
    {
        schedule.Failures = Math.Min(schedule.Failures + 1, 32);
        schedule.NextPollAt = now + FailureDelay(schedule.Failures, _jitter());
        _registry.MarkExternalFailure(instance, now, errorKind);
    }

    internal static TimeSpan FailureDelay(int failureCount, double jitterSample)
    {
        var exponent = Math.Clamp(failureCount - 1, 0, 3);
        var seconds = Math.Min(30, 5 * (1 << exponent));
        var boundedJitter = Math.Clamp(jitterSample, 0d, 1d);
        return TimeSpan.FromSeconds(seconds * (1d + 0.2d * boundedJitter));
    }

    private sealed class PollSchedule(MediaServerInstance instance)
    {
        public MediaServerInstance Instance { get; set; } = instance;
        public DateTimeOffset NextPollAt { get; set; } = DateTimeOffset.MinValue;
        public int Failures { get; set; }
    }
}
