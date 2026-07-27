using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models.Metrics;
using NzbWebDAV.Logging;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Streams;
using Serilog;

namespace NzbWebDAV.Services.SupportPack;

public sealed class SupportPackService(
    LogBufferSink logBuffer,
    ConfigManager configManager,
    MetricsWriter metricsWriter,
    ProviderBytesTracker bytesTracker,
    UsenetStreamingClient usenetStreamingClient,
    ArticleMissNegativeCache articleMissCache,
    InFlightArticleBudget inFlightArticleBudget)
{
    private const long MinuteMs = 60_000;
    private const long HourMs = 60 * MinuteMs;
    private const long DayMs = 24 * HourMs;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal async Task WriteAsync(Stream output, CancellationToken cancellationToken)
    {
        var generatedAt = DateTimeOffset.UtcNow;
        var config = configManager.GetDiagnosticSnapshot();
        var redactor = new SupportPackRedactor(CollectSecrets(config));
        var logSnapshot = logBuffer.Snapshot(logBuffer.Capacity, null, null, null, null);
        var sectionStatus = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["logs"] = "included",
            ["configuration"] = "included",
            ["environment"] = "included",
        };

        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        await WriteTextAsync(archive, "README.txt", BuildReadme(), cancellationToken).ConfigureAwait(false);
        await WriteTextAsync(
            archive,
            "logs/backend.log",
            redactor.RedactText(FormatLogs(logSnapshot.Entries)),
            cancellationToken).ConfigureAwait(false);
        await WriteJsonAsync(
            archive,
            "configuration.json",
            BuildConfiguration(config, redactor),
            redactor,
            cancellationToken).ConfigureAwait(false);
        await WriteJsonAsync(
            archive,
            "environment.json",
            BuildEnvironment(generatedAt),
            redactor,
            cancellationToken).ConfigureAwait(false);

        try
        {
            var metrics = await BuildMetricsAsync(generatedAt, cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(archive, "metrics/recent.json", metrics, redactor, cancellationToken)
                .ConfigureAwait(false);
            sectionStatus["metrics"] = "included";
        }
        catch
        {
            sectionStatus["metrics"] = "unavailable";
        }

        await WriteJsonAsync(
            archive,
            "manifest.json",
            await BuildManifestAsync(generatedAt, logSnapshot, sectionStatus, redactor, cancellationToken)
                .ConfigureAwait(false),
            redactor,
            cancellationToken).ConfigureAwait(false);
    }

    internal static string BuildReadme() =>
        """
        NzbDAV technical support pack

        This archive contains the current backend in-memory log buffer, a redacted
        active Settings snapshot, runtime information, and aggregate metrics.
        Backend logs are cleared when NzbDAV restarts. Frontend and container logs
        are not included.

        The archive deliberately excludes database files, backups, blobs/NZBs,
        environment files, session/API key files, crash dumps, stream traces, and
        segment-cache data. Credentials, API keys, tokens, URL credentials and
        sensitive URL query values are redacted. IP addresses are pseudonymized.

        File names, filesystem paths, account usernames, DNS hostnames, and
        non-secret URL paths can remain for troubleshooting. Share this archive only
        with trusted NzbDAV support.
        """;

    private static object BuildConfiguration(
        IReadOnlyList<ConfigDiagnosticSnapshot> config,
        SupportPackRedactor redactor) =>
        new
        {
            settings = config.Select(item => new
            {
                key = item.Key,
                value = redactor.RedactConfigurationValue(item.Key, item.Value),
                source = item.Source,
                environmentVariable = item.EnvironmentVariableName,
            }),
        };

    private object BuildEnvironment(DateTimeOffset generatedAt)
    {
        ThreadPool.GetMinThreads(out var minWorkerThreads, out var minIoThreads);
        ThreadPool.GetMaxThreads(out var maxWorkerThreads, out var maxIoThreads);
        var configPath = DavDatabaseContext.ConfigPath;
        var root = Path.GetPathRoot(Path.GetFullPath(configPath)) ?? configPath;
        var drive = new DriveInfo(root);
        var uptime = ProcessUptime();

        return new
        {
            generatedAtUtc = generatedAt,
            appVersion = ConfigManager.AppVersion,
            commit = Environment.GetEnvironmentVariable("NZBDAV_COMMIT_SHA"),
            uptimeSeconds = (long)uptime.TotalSeconds,
            processStartedAtUtc = generatedAt - uptime,
            runtime = new
            {
                framework = RuntimeInformation.FrameworkDescription,
                os = RuntimeInformation.OSDescription,
                osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                processorCount = Environment.ProcessorCount,
                workingSetBytes = Environment.WorkingSet,
                gcTotalMemoryBytes = GC.GetTotalMemory(forceFullCollection: false),
                inFlightArticleBytes = inFlightArticleBudget.LeasedBytes,
                inFlightArticleBudgetBytes = inFlightArticleBudget.CapBytes,
                inFlightArticleThrottleEvents = inFlightArticleBudget.ThrottleEvents,
                timeZone = TimeZoneInfo.Local.Id,
            },
            threadPool = new { minWorkerThreads, minIoThreads, maxWorkerThreads, maxIoThreads },
            storage = new
            {
                configPath,
                configDatabaseBytes = FileSize(DavDatabaseContext.DatabaseFilePath),
                metricsDatabaseBytes = FileSize(MetricsDbContext.DatabaseFilePath),
                availableFreeSpaceBytes = drive.IsReady ? drive.AvailableFreeSpace : (long?)null,
            },
            environment = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["LOG_LEVEL"] = Environment.GetEnvironmentVariable("LOG_LEVEL"),
                ["LOG_BUFFER_SIZE"] = Environment.GetEnvironmentVariable("LOG_BUFFER_SIZE"),
                ["STREAM_TRACE_EVENTS"] = Environment.GetEnvironmentVariable("STREAM_TRACE_EVENTS"),
                ["TZ"] = Environment.GetEnvironmentVariable("TZ"),
                ["PUID"] = Environment.GetEnvironmentVariable("PUID"),
                ["PGID"] = Environment.GetEnvironmentVariable("PGID"),
                ["MAX_REQUEST_BODY_SIZE"] = Environment.GetEnvironmentVariable("MAX_REQUEST_BODY_SIZE"),
            },
        };
    }

    private async Task<object> BuildMetricsAsync(DateTimeOffset generatedAt, CancellationToken cancellationToken)
    {
        var now = generatedAt.ToUnixTimeMilliseconds();
        var since24Hours = now - DayMs;
        var since7Days = now - 7 * DayMs;
        var providers = configManager.GetUsenetProviderConfig().Providers;
        var nicknames = providers
            .Where(provider => provider.ProviderId != Guid.Empty)
            .ToDictionary(
                UsenetProviderIdentity.MetricsKey,
                provider => string.IsNullOrWhiteSpace(provider.Nickname) ? null : provider.Nickname,
                StringComparer.Ordinal);

        await using var db = new MetricsDbContext();
        var minuteRows = await db.ThroughputMinutes
            .Where(row => row.Minute >= since24Hours)
            .Select(row => new
            {
                row.Minute,
                row.Articles,
                row.Misses,
                row.Errors,
                row.BytesFetched,
                row.BytesServed,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var providerHours = await db.ProviderHourly
            .Where(row => row.Hour >= since7Days)
            .Select(row => new
            {
                row.Hour,
                row.Provider,
                row.Articles,
                row.Misses,
                row.Errors,
                row.Retries,
                row.BytesFetched,
                row.FailoverSaves,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var circuitTransitions = await db.MetricEvents
            .Where(row => row.Kind == "circuit" && row.At >= since7Days)
            .Select(row => new { row.At, row.Tag1, row.Tag2, row.Num })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var failover = await db.FailoverHourly
            .Where(row => row.Hour >= since7Days)
            .GroupBy(row => row.Reason)
            .Select(group => new { reason = group.Key.ToString(), count = group.Sum(row => row.Count) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var usageHours = await ProviderUsageHelper.ReadRecentHoursAsync(
            providers.Where(provider => provider.ProviderId != Guid.Empty).Select(UsenetProviderIdentity.MetricsKey))
            .ConfigureAwait(false);
        var usage = providers
            .Where(provider => provider.ProviderId != Guid.Empty)
            .Select(provider =>
            {
                var key = UsenetProviderIdentity.MetricsKey(provider);
                usageHours.TryGetValue(key, out var hours);
                var bytesUsed = ProviderUsageHelper.ComputeUsage(bytesTracker, provider);
                var (bytesPerDay, daysRemaining) = ProviderUsageHelper.ComputeBurnRate(provider, bytesUsed, hours);
                return new
                {
                    providerKey = key,
                    nickname = nicknames.GetValueOrDefault(key),
                    bytesUsed,
                    byteLimit = provider.ByteLimit,
                    overLimit = ProviderUsageHelper.IsOverLimit(bytesTracker, provider),
                    bytesPerDay,
                    daysRemaining,
                };
            })
            .ToList();

        var circuitStates = usenetStreamingClient.GetProviderCircuitSnapshots()
            .Select(snapshot => new
            {
                providerKey = snapshot.MetricsKey,
                nickname = nicknames.GetValueOrDefault(snapshot.MetricsKey),
                state = snapshot.Breaker.State.ToString(),
                snapshot.Breaker.CooldownRemainingSeconds,
                snapshot.Breaker.LastFailureReason,
                snapshot.Breaker.TripCount,
                snapshot.Breaker.FailureCount,
                snapshot.Breaker.ArticleMissCount,
            })
            .ToList();
        var stats = metricsWriter.Stats;
        return new
        {
            generatedAtUtc = generatedAt,
            outage24Hours = new
            {
                bucketSizeMs = 5 * MinuteMs,
                throughput = minuteRows
                    .GroupBy(row => row.Minute - row.Minute % (5 * MinuteMs))
                    .OrderBy(group => group.Key)
                    .Select(group => new
                    {
                        bucket = group.Key,
                        articles = group.Sum(row => row.Articles),
                        misses = group.Sum(row => row.Misses),
                        errors = group.Sum(row => row.Errors),
                        bytesFetched = group.Sum(row => row.BytesFetched),
                        bytesServed = group.Sum(row => row.BytesServed),
                    }),
            },
            consumption7Days = new
            {
                providerHours = providerHours
                    .OrderBy(row => row.Hour)
                    .Select(row => new
                    {
                        row.Hour,
                        providerKey = row.Provider,
                        nickname = nicknames.GetValueOrDefault(row.Provider),
                        row.Articles,
                        row.Misses,
                        row.Errors,
                        row.Retries,
                        row.BytesFetched,
                        row.FailoverSaves,
                    }),
                providerUsage = usage,
            },
            circuits = new
            {
                current = circuitStates,
                transitions = circuitTransitions
                    .Where(row => row.Tag1 is not null && row.Tag2 is not null)
                    .Select(row => new
                    {
                        at = row.At,
                        providerKey = row.Tag1,
                        nickname = nicknames.GetValueOrDefault(row.Tag1!),
                        state = row.Tag2,
                        cooldownMs = row.Num,
                    }),
            },
            failoverReasons = failover,
            articleMissCache = new
            {
                hits = articleMissCache.Hits,
                skips = articleMissCache.Skips,
                entries = articleMissCache.Entries,
            },
            metricsHealth = new
            {
                queued = stats.QueuedFetches + stats.QueuedEvents + stats.QueuedSessions + stats.QueuedFailoverMisses,
                dropped = stats.DroppedFetches + stats.DroppedEvents + stats.DroppedSessions + stats.DroppedFailoverMisses,
                stats.LastSuccessfulFlushAtMs,
                stats.LastFlushError,
            },
        };
    }

    private async Task<object> BuildManifestAsync(
        DateTimeOffset generatedAt,
        LogSnapshot logs,
        IReadOnlyDictionary<string, string> sectionStatus,
        SupportPackRedactor redactor,
        CancellationToken cancellationToken)
    {
        var (mainMigration, metricsMigration) = await ReadMigrationsAsync(cancellationToken).ConfigureAwait(false);
        return new
        {
            schemaVersion = 1,
            generatedAtUtc = generatedAt,
            appVersion = ConfigManager.AppVersion,
            commit = Environment.GetEnvironmentVariable("NZBDAV_COMMIT_SHA"),
            migrations = new { main = mainMigration, metrics = metricsMigration },
            logs = new { count = logs.Entries.Count, logs.OldestSequence, logs.NewestSequence, capacity = logBuffer.Capacity },
            sections = sectionStatus,
            redaction = new { secrets = redactor.SecretsRedacted, ipAddresses = redactor.AddressesPseudonymized },
        };
    }

    private static async Task<(string? Main, string? Metrics)> ReadMigrationsAsync(CancellationToken cancellationToken)
    {
        async Task<string?> ReadAsync(DbContext db)
        {
            try
            {
                return (await db.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false))
                    .LastOrDefault();
            }
            catch
            {
                return null;
            }
        }

        await using var main = new DavDatabaseContext();
        await using var metrics = new MetricsDbContext();
        return (await ReadAsync(main).ConfigureAwait(false), await ReadAsync(metrics).ConfigureAwait(false));
    }

    private static IEnumerable<string?> CollectSecrets(IEnumerable<ConfigDiagnosticSnapshot> config)
    {
        foreach (var item in config)
        {
            if (item.Value is null)
                continue;
            if (item.Key is ConfigKeys.ApiKey or ConfigKeys.ApiStrmKey or ConfigKeys.RclonePass
                or ConfigKeys.WebdavPass or ConfigKeys.WatchtowerProfileToken)
            {
                yield return item.Value;
                continue;
            }

            if (item.Key is not (ConfigKeys.UsenetProviders or ConfigKeys.ArrInstances
                or ConfigKeys.IndexersInstances or ConfigKeys.ProfilesInstances))
                continue;

            List<string>? structuredSecrets = null;
            try
            {
                using var document = JsonDocument.Parse(item.Value);
                structuredSecrets = CollectJsonSecrets(document.RootElement).ToList();
            }
            catch (JsonException)
            {
                // The structured value will be omitted by the redactor.
            }

            if (structuredSecrets is not null)
                foreach (var secret in structuredSecrets)
                    yield return secret;
        }

        yield return Environment.GetEnvironmentVariable("FRONTEND_BACKEND_API_KEY");
        yield return Environment.GetEnvironmentVariable("WEBDAV_PASSWORD");
        yield return Environment.GetEnvironmentVariable("SESSION_KEY");
    }

    private static IEnumerable<string> CollectJsonSecrets(JsonElement element, string? propertyName = null)
    {
        var normalized = propertyName?.Replace("-", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal)
            .ToLowerInvariant();
        if (normalized is "apikey" or "pass" or "password" or "token")
        {
            if (element.ValueKind == JsonValueKind.String)
                yield return element.GetString()!;
            yield break;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
                foreach (var secret in CollectJsonSecrets(property.Value, property.Name))
                    yield return secret;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                foreach (var secret in CollectJsonSecrets(item))
                    yield return secret;
        }
    }

    private static async Task WriteJsonAsync(
        ZipArchive archive,
        string name,
        object value,
        SupportPackRedactor redactor,
        CancellationToken cancellationToken) =>
        await WriteTextAsync(
            archive,
            name,
            redactor.RedactText(JsonSerializer.Serialize(value, JsonOptions)),
            cancellationToken).ConfigureAwait(false);

    private static async Task WriteTextAsync(
        ZipArchive archive,
        string name,
        string content,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        await using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static long? FileSize(string path) =>
        File.Exists(path) ? new FileInfo(path).Length : null;

    /// <summary>
    /// Real uptime of the backend process. This used to be measured from this service's
    /// own construction, but DI creates it lazily on the first support-pack download, so a
    /// backend running for hours reported a few seconds and made the log buffer's
    /// timestamps look impossible. Process start time is absolute, so reading it on demand
    /// stays accurate. Note that Environment.TickCount64 is unusable here: inside a
    /// container it reports the host's uptime, not this process's.
    /// </summary>
    private static TimeSpan ProcessUptime()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var uptime = DateTimeOffset.UtcNow - process.StartTime.ToUniversalTime();
            if (uptime > TimeSpan.Zero) return uptime;
        }
        catch (Exception e)
        {
            Log.Debug(e, "Support pack: could not read the process start time");
        }

        return TimeSpan.Zero;
    }

    private static string FormatLogs(IEnumerable<LogEntry> entries)
    {
        var builder = new StringBuilder();
        foreach (var entry in entries)
        {
            builder.Append('[')
                .Append(DateTimeOffset.FromUnixTimeMilliseconds(entry.TimestampUnixMs)
                    .ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append("] [").Append(entry.Level).Append(']');
            if (entry.Source is not null)
                builder.Append(" [").Append(entry.Source).Append(']');
            builder.Append(' ').AppendLine(entry.Message);
            if (entry.Exception is not null)
                builder.AppendLine(entry.Exception);
        }
        return builder.ToString();
    }
}
