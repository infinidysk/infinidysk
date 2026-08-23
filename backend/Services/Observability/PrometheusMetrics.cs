using Prometheus;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Services.Metrics;
using NzbWebDAV.Streams;

namespace NzbWebDAV.Services.Observability;

/// <summary>
/// Bounded Prometheus metrics for live streaming and provider health.
/// </summary>
public sealed class PrometheusMetrics
{
    private readonly Gauge _activeReads;
    private readonly Counter _bytesServed;
    private readonly Counter _readStarts;
    private readonly Counter _readOverlaps;
    private readonly Counter _duplicateSegmentFetches;
    private readonly Gauge _overlappingPaths;
    private readonly Gauge _inFlightSegmentFetches;
    private readonly Gauge _articleBudgetBytes;
    private readonly Gauge _articleBudgetCapBytes;
    private readonly Counter _articleBudgetThrottleEvents;
    private readonly Gauge _metricsQueueLength;
    private readonly Counter _metricsDropped;
    private readonly Gauge _poolConnections;
    private readonly Gauge _poolMaxConnections;
    private readonly Gauge _poolChurn;
    private readonly Gauge _circuitState;
    private readonly Gauge _circuitCooldownSeconds;
    private readonly Gauge _circuitTrips;
    private readonly Gauge _circuitFailures;
    private readonly Gauge _circuitArticleMisses;
    private readonly Counter _segmentFetches;
    private readonly Histogram _segmentFetchDuration;
    private readonly Counter _seekCount;
    private readonly Histogram _seekLatency;
    private readonly Histogram _par2RepairDuration;
    private readonly Counter _par2RepairBytesRead;
    private readonly Counter _par2SlicesReconstructed;
    private readonly Counter _par2SegmentsCommitted;
    private readonly Counter _par2ValidationFailures;
    private readonly Gauge _par2PatchStoreBytes;
    private readonly Counter _par2PatchHits;
    private readonly Counter _par2PatchEvictions;
    private readonly Counter _par2RepairJobs;
    private readonly Gauge _sharedStreamRingBytes;
    private readonly Gauge _sharedStreamRingBytesPeak;
    private readonly Gauge _sharedStreamRingLogicalBytes;
    private readonly Gauge _sharedStreamPumpScratchBytes;
    private readonly Gauge _sharedStreamLiveEntries;
    private readonly Gauge _sharedStreamReadyEntries;
    private readonly Gauge _sharedStreamDrainingEntries;
    private readonly Gauge _sharedStreamLaggingReaders;
    private readonly Counter _sharedStreamPressureDetaches;
    private readonly Counter _sharedStreamPressureReaps;
    private readonly Counter _sharedStreamAttachHits;
    private readonly Counter _sharedStreamAttachMisses;
    private readonly Counter _sharedStreamEntriesCreated;
    private readonly Counter _sharedStreamReadersServed;
    private readonly Counter _privateFallbacks;
    private readonly Counter _streamingCorruptSegments;
    private readonly HashSet<string> _providerKeys = new(StringComparer.Ordinal);

    public PrometheusMetrics(CollectorRegistry registry)
    {
        var metrics = Prometheus.Metrics.WithCustomRegistry(registry);
        _activeReads = metrics.CreateGauge("nzbdav_active_reads", "Current active read sessions.");
        _bytesServed = metrics.CreateCounter("nzbdav_bytes_served_total", "Bytes served to readers.");
        _readStarts = metrics.CreateCounter("nzbdav_concurrent_read_starts_total", "Read starts.", new CounterConfiguration { LabelNames = ["region"] });
        _readOverlaps = metrics.CreateCounter("nzbdav_concurrent_read_overlap_events_total", "Overlapping read opportunities.");
        _duplicateSegmentFetches = metrics.CreateCounter("nzbdav_concurrent_read_duplicate_segment_fetches_total", "Duplicate in-flight segment fetches.");
        _overlappingPaths = metrics.CreateGauge("nzbdav_concurrent_overlapping_paths", "Current paths with overlapping readers.");
        _inFlightSegmentFetches = metrics.CreateGauge("nzbdav_concurrent_in_flight_segment_fetches", "Current in-flight segment fetches.");
        _articleBudgetBytes = metrics.CreateGauge("nzbdav_inflight_article_bytes", "Article bytes currently leased.");
        _articleBudgetCapBytes = metrics.CreateGauge("nzbdav_inflight_article_budget_bytes", "Configured in-flight article byte budget.");
        _articleBudgetThrottleEvents = metrics.CreateCounter(
            "nzbdav_inflight_article_throttle_events_total",
            "Article RAM lease requests that encountered budget backpressure.");
        _metricsQueueLength = metrics.CreateGauge("nzbdav_metrics_queue_length", "Queued internal metric rows.", new GaugeConfiguration { LabelNames = ["queue"] });
        _metricsDropped = metrics.CreateCounter("nzbdav_metrics_dropped_total", "Dropped internal metric rows.", new CounterConfiguration { LabelNames = ["queue"] });
        _poolConnections = metrics.CreateGauge("nzbdav_nntp_pool_connections", "NNTP pool connection state.", new GaugeConfiguration { LabelNames = ["provider_key", "state"] });
        _poolMaxConnections = metrics.CreateGauge("nzbdav_nntp_pool_max_connections", "NNTP pool connection limits.", new GaugeConfiguration { LabelNames = ["provider_key", "limit"] });
        _poolChurn = metrics.CreateGauge("nzbdav_nntp_pool_churn_total", "NNTP pool lifetime churn.", new GaugeConfiguration { LabelNames = ["provider_key", "event"] });
        _circuitState = metrics.CreateGauge("nzbdav_circuit_state", "Circuit state: 0=closed, 1=open, 2=half_open.", new GaugeConfiguration { LabelNames = ["provider_key"] });
        _circuitCooldownSeconds = metrics.CreateGauge("nzbdav_circuit_cooldown_remaining_seconds", "Circuit cooldown remaining.", new GaugeConfiguration { LabelNames = ["provider_key"] });
        _circuitTrips = metrics.CreateGauge("nzbdav_circuit_trips_total", "Circuit trips.", new GaugeConfiguration { LabelNames = ["provider_key"] });
        _circuitFailures = metrics.CreateGauge("nzbdav_circuit_failures_total", "Circuit failures.", new GaugeConfiguration { LabelNames = ["provider_key"] });
        _circuitArticleMisses = metrics.CreateGauge("nzbdav_circuit_article_misses_total", "Circuit article misses.", new GaugeConfiguration { LabelNames = ["provider_key"] });
        _segmentFetches = metrics.CreateCounter("nzbdav_segment_fetches_total", "Segment fetch outcomes.", new CounterConfiguration { LabelNames = ["provider_key", "status"] });
        _segmentFetchDuration = metrics.CreateHistogram("nzbdav_segment_fetch_duration_seconds", "Segment fetch duration.", new HistogramConfiguration { LabelNames = ["provider_key"], Buckets = Histogram.ExponentialBuckets(0.01, 2, 14) });
        _seekCount = metrics.CreateCounter("nzbdav_seek_total", "Seek operations.", new CounterConfiguration { LabelNames = ["kind"] });
        _seekLatency = metrics.CreateHistogram("nzbdav_seek_latency_seconds", "Post-seek preparation latency.", new HistogramConfiguration { LabelNames = ["kind"], Buckets = Histogram.ExponentialBuckets(0.001, 2, 14) });
        _par2RepairJobs = metrics.CreateCounter(
            "nzbdav_par2_repair_jobs_total",
            "PAR2 background repair job state transitions.",
            new CounterConfiguration { LabelNames = ["state"] });
        _par2RepairDuration = metrics.CreateHistogram(
            "nzbdav_par2_repair_duration_seconds",
            "PAR2 background repair job duration.",
            new HistogramConfiguration { Buckets = Histogram.ExponentialBuckets(1, 2, 14) });
        _par2RepairBytesRead = metrics.CreateCounter(
            "nzbdav_par2_repair_bytes_read_total",
            "NNTP bytes read during PAR2 repairs.");
        _par2SlicesReconstructed = metrics.CreateCounter(
            "nzbdav_par2_repair_slices_reconstructed_total",
            "PAR2 slices reconstructed from recovery data.");
        _par2SegmentsCommitted = metrics.CreateCounter(
            "nzbdav_par2_repair_segments_committed_total",
            "Segments committed to the repair patch store by PAR2 repair.");
        _par2ValidationFailures = metrics.CreateCounter(
            "nzbdav_par2_validation_failures_total",
            "PAR2 validation gate failures.",
            new CounterConfiguration { LabelNames = ["gate"] });
        _par2PatchStoreBytes = metrics.CreateGauge(
            "nzbdav_par2_patch_store_bytes",
            "Current repair patch store size in bytes.");
        _par2PatchHits = metrics.CreateCounter(
            "nzbdav_par2_patch_hits_total",
            "Segment fetches served from the repair patch store.");
        _par2PatchEvictions = metrics.CreateCounter(
            "nzbdav_par2_patch_evictions_total",
            "Repair patch store evictions.");
        _sharedStreamRingBytes = metrics.CreateGauge(
            "nzbdav_shared_stream_ring_retained_bytes",
            "ArrayPool capacity currently rented by shared-stream rings. Returning buffers to the pool does not release those pages to the OS.");
        _sharedStreamRingBytesPeak = metrics.CreateGauge(
            "nzbdav_shared_stream_ring_retained_bytes_peak",
            "Peak ArrayPool capacity rented by shared-stream rings.");
        _sharedStreamRingLogicalBytes = metrics.CreateGauge(
            "nzbdav_shared_stream_ring_logical_bytes",
            "Logical decoded bytes currently stored in shared-stream rings.");
        _sharedStreamPumpScratchBytes = metrics.CreateGauge(
            "nzbdav_shared_stream_pump_scratch_bytes",
            "ArrayPool capacity currently rented by shared-stream pump scratch buffers.");
        _sharedStreamLiveEntries = metrics.CreateGauge(
            "nzbdav_shared_stream_live_entries",
            "Live shared-stream region entries.");
        _sharedStreamReadyEntries = metrics.CreateGauge(
            "nzbdav_shared_stream_ready_entries",
            "Shared-stream entries currently serving readers.");
        _sharedStreamDrainingEntries = metrics.CreateGauge(
            "nzbdav_shared_stream_draining_entries",
            "Shared-stream entries in the last-reader grace period.");
        _sharedStreamLaggingReaders = metrics.CreateGauge(
            "nzbdav_shared_stream_lagging_readers",
            "Shared-stream readers more than lead-bytes behind the fastest cursor.");
        _sharedStreamPressureDetaches = metrics.CreateCounter(
            "nzbdav_shared_stream_pressure_detaches_total",
            "Readers detached because shared-stream retention pressure required a private fallback.");
        _sharedStreamPressureReaps = metrics.CreateCounter(
            "nzbdav_shared_stream_pressure_reaps_total",
            "Shared-stream entries reaped because of retention pressure.");
        _sharedStreamAttachHits = metrics.CreateCounter(
            "nzbdav_shared_stream_attach_hits_total",
            "Requests served from an existing shared stream.");
        _sharedStreamAttachMisses = metrics.CreateCounter(
            "nzbdav_shared_stream_attach_misses_total",
            "Requests that did not attach to a shared stream.");
        _sharedStreamEntriesCreated = metrics.CreateCounter(
            "nzbdav_shared_stream_entries_created_total",
            "Shared stream region entries created.");
        _sharedStreamReadersServed = metrics.CreateCounter(
            "nzbdav_shared_stream_readers_served_total",
            "Readers attached to a shared stream, including the creator.");
        _privateFallbacks = metrics.CreateCounter(
            "nzbdav_concurrent_read_private_fallbacks_total",
            "Overlapping reads that used a private stream.");
        _streamingCorruptSegments = metrics.CreateCounter(
            "nzbdav_streaming_corrupt_segments_total",
            "Streaming-confirmed corrupt Usenet articles.");
    }

    public static PrometheusMetrics? Current { get; set; }

    public void RecordSegmentFetch(string providerKey, string status, TimeSpan duration)
    {
        _segmentFetches.WithLabels(providerKey, status).Inc();
        _segmentFetchDuration.WithLabels(providerKey).Observe(duration.TotalSeconds);
    }

    public void RecordSeek(string kind, TimeSpan elapsed)
    {
        _seekCount.WithLabels(kind).Inc();
        _seekLatency.WithLabels(kind).Observe(elapsed.TotalSeconds);
    }

    public void RecordPar2RepairJob(string state) => _par2RepairJobs.WithLabels(state).Inc();

    public void ObservePar2RepairDuration(TimeSpan elapsed)
        => _par2RepairDuration.Observe(elapsed.TotalSeconds);

    public void AddPar2RepairBytesRead(long bytes) => _par2RepairBytesRead.Inc(bytes);

    public void AddPar2SlicesReconstructed(int count) => _par2SlicesReconstructed.Inc(count);

    public void AddPar2SegmentsCommitted(int count) => _par2SegmentsCommitted.Inc(count);

    public void RecordPar2ValidationFailure(string gate) => _par2ValidationFailures.WithLabels(gate).Inc();

    public void SetPar2PatchStoreBytes(long bytes) => _par2PatchStoreBytes.Set(bytes);

    public void RecordPar2PatchHit() => _par2PatchHits.Inc();

    public void RecordPar2PatchEviction() => _par2PatchEvictions.Inc();

    public void RecordStreamingCorruptSegment() => _streamingCorruptSegments.Inc();

    public void Refresh(
        ActiveReadRegistry activeReads,
        ConcurrentReadTracker concurrentReads,
        InFlightArticleBudget articleBudget,
        MetricsWriter metricsWriter,
        UsenetStreamingClient usenetClient)
    {
        _activeReads.Set(activeReads.Count);
        _bytesServed.IncTo(activeReads.TotalBytesServed);

        var reads = concurrentReads.Snapshot();
        _readStarts.WithLabels("full").IncTo(reads.FullReads);
        _readStarts.WithLabels("start_range").IncTo(reads.StartRangeReads);
        _readStarts.WithLabels("offset_range").IncTo(reads.OffsetRangeReads);
        _readStarts.WithLabels("suffix_range").IncTo(reads.SuffixRangeReads);
        _readOverlaps.IncTo(reads.OverlapEvents);
        _duplicateSegmentFetches.IncTo(reads.DuplicateInFlightSegmentFetches);
        _privateFallbacks.IncTo(reads.PrivateFallbacksNoRegistry);
        _sharedStreamAttachHits.IncTo(reads.SharedAttachHits);
        _sharedStreamAttachMisses.IncTo(reads.SharedAttachMisses);
        _sharedStreamEntriesCreated.IncTo(reads.SharedEntriesCreated);
        _sharedStreamReadersServed.IncTo(reads.SharedReadersServedTotal);
        _sharedStreamRingBytes.Set(reads.SharedStreamRingRetainedBytes);
        _sharedStreamRingBytesPeak.Set(reads.SharedStreamRingRetainedBytesPeak);
        _sharedStreamRingLogicalBytes.Set(reads.SharedStreamRingLogicalBytes);
        _sharedStreamPumpScratchBytes.Set(reads.SharedStreamPumpScratchRentedBytes);
        _sharedStreamLiveEntries.Set(reads.SharedStreamLiveEntries);
        _sharedStreamReadyEntries.Set(reads.SharedStreamReadyEntries);
        _sharedStreamDrainingEntries.Set(reads.SharedStreamDrainingEntries);
        _sharedStreamLaggingReaders.Set(reads.SharedStreamLaggingReaders);
        _sharedStreamPressureDetaches.IncTo(reads.SharedStreamPressureDetaches);
        _sharedStreamPressureReaps.IncTo(reads.SharedStreamPressureReaps);
        _overlappingPaths.Set(reads.CurrentOverlappingPaths);
        _inFlightSegmentFetches.Set(reads.CurrentInFlightSegmentFetches);

        _articleBudgetBytes.Set(articleBudget.LeasedBytes);
        _articleBudgetCapBytes.Set(articleBudget.CapBytes);
        _articleBudgetThrottleEvents.IncTo(articleBudget.ThrottleEvents);

        var writer = metricsWriter.Stats;
        _metricsQueueLength.WithLabels("fetches").Set(writer.QueuedFetches);
        _metricsQueueLength.WithLabels("events").Set(writer.QueuedEvents);
        _metricsQueueLength.WithLabels("sessions").Set(writer.QueuedSessions);
        _metricsQueueLength.WithLabels("failover_misses").Set(writer.QueuedFailoverMisses);
        _metricsDropped.WithLabels("fetches").IncTo(writer.DroppedFetches);
        _metricsDropped.WithLabels("events").IncTo(writer.DroppedEvents);
        _metricsDropped.WithLabels("sessions").IncTo(writer.DroppedSessions);
        _metricsDropped.WithLabels("failover_misses").IncTo(writer.DroppedFailoverMisses);

        var currentKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pool in usenetClient.GetProviderConnectionSnapshots())
        {
            currentKeys.Add(pool.MetricsKey);
            SetPool(pool);
        }
        foreach (var circuit in usenetClient.GetProviderCircuitSnapshots())
        {
            currentKeys.Add(circuit.MetricsKey);
            SetCircuit(circuit);
        }
        foreach (var stale in _providerKeys.Except(currentKeys).ToArray())
            RemoveProvider(stale);
        _providerKeys.Clear();
        _providerKeys.UnionWith(currentKeys);
    }

    private void SetPool(ProviderConnectionSnapshot pool)
    {
        var key = pool.MetricsKey;
        _poolConnections.WithLabels(key, "live").Set(pool.LiveConnections);
        _poolConnections.WithLabels(key, "idle").Set(pool.IdleConnections);
        _poolConnections.WithLabels(key, "active").Set(pool.ActiveConnections);
        _poolConnections.WithLabels(key, "available").Set(pool.AvailableConnections);
        _poolConnections.WithLabels(key, "pending").Set(pool.PendingSelections);
        _poolMaxConnections.WithLabels(key, "effective").Set(pool.EffectiveMaxConnections);
        if (pool.LearnedConnectionLimit is { } learned)
            _poolMaxConnections.WithLabels(key, "learned").Set(learned);
        _poolChurn.WithLabels(key, "opened").Set(pool.Churn.ConnectionsOpened);
        _poolChurn.WithLabels(key, "reused").Set(pool.Churn.ConnectionsReused);
        _poolChurn.WithLabels(key, "destroyed").Set(pool.Churn.ConnectionsDestroyed);
        _poolChurn.WithLabels(key, "stale_eviction").Set(pool.Churn.StaleEvictions);
        _poolChurn.WithLabels(key, "handshake_failure").Set(pool.Churn.HandshakeFailures);
    }

    private void SetCircuit(ProviderCircuitRuntimeSnapshot circuit)
    {
        var breaker = circuit.Breaker;
        _circuitState.WithLabels(circuit.MetricsKey).Set(breaker.State switch
        {
            ProviderCircuitState.Open => 1,
            ProviderCircuitState.HalfOpen => 2,
            _ => 0,
        });
        _circuitCooldownSeconds.WithLabels(circuit.MetricsKey).Set(breaker.CooldownRemainingSeconds ?? 0);
        _circuitTrips.WithLabels(circuit.MetricsKey).Set(breaker.TripCount);
        _circuitFailures.WithLabels(circuit.MetricsKey).Set(breaker.FailureCount);
        _circuitArticleMisses.WithLabels(circuit.MetricsKey).Set(breaker.ArticleMissCount);
    }

    private void RemoveProvider(string key)
    {
        foreach (var state in new[] { "live", "idle", "active", "available", "pending" })
            _poolConnections.RemoveLabelled(key, state);
        foreach (var limit in new[] { "effective", "learned" })
            _poolMaxConnections.RemoveLabelled(key, limit);
        foreach (var churn in new[] { "opened", "reused", "destroyed", "stale_eviction", "handshake_failure" })
            _poolChurn.RemoveLabelled(key, churn);
        _circuitState.RemoveLabelled(key);
        _circuitCooldownSeconds.RemoveLabelled(key);
        _circuitTrips.RemoveLabelled(key);
        _circuitFailures.RemoveLabelled(key);
        _circuitArticleMisses.RemoveLabelled(key);
    }
}
