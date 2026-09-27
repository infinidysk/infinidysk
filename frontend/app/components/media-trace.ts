/** Subset of the backend StreamTraceEvent payload used by the diagnostics drawer. */
export type StreamTraceEvent = {
  seq: number;
  at: number;
  kind: string;
  rangeStart?: number | null;
  rangeEnd?: number | null;
  offset?: number | null;
  provider?: string | null;
  status?: string | null;
  durationMs?: number | null;
  retries?: number | null;
  segmentId?: string | null;
  bytes?: number | null;
  endReason?: string | null;
  bytesServed?: number | null;
  fromProvider?: string | null;
  toProvider?: string | null;
  attempt?: number | null;
  message?: string | null;
  batchSize?: number | null;
  previousBatchSize?: number | null;
  rangeGeneration?: number | null;
  connWaitMs?: number | null;
  providerWaitMs?: number | null;
  bodyDrainMs?: number | null;
  consumerWaitMs?: number | null;
  clientWriteMs?: number | null;
  connOpened?: number | null;
  connReused?: number | null;
  fetches?: number | null;
};

export type TraceSummary = {
  rangeOpens: number;
  rangeEnds: number;
  seeks: number;
  segments: number;
  segmentsByStatus: Record<string, number>;
  zeroFills: number;
  failovers: number;
  retries: number;
  prefetchChanges: number;
  startups: number;
  lastStartupPhase: string | null;
  bytesServed: number;
  lastEndReason: string | null;
  lastEndMessage: string | null;
  stallTotalsMs: Record<string, number> | null;
};

export function summarizeTrace(events: StreamTraceEvent[]): TraceSummary {
  const summary: TraceSummary = {
    rangeOpens: 0,
    rangeEnds: 0,
    seeks: 0,
    segments: 0,
    segmentsByStatus: {},
    zeroFills: 0,
    failovers: 0,
    retries: 0,
    prefetchChanges: 0,
    startups: 0,
    lastStartupPhase: null,
    bytesServed: 0,
    lastEndReason: null,
    lastEndMessage: null,
    stallTotalsMs: null,
  };
  for (const e of events) {
    switch (e.kind) {
      case "RangeOpen":
        summary.rangeOpens++;
        break;
      case "RangeEnd":
        summary.rangeEnds++;
        summary.bytesServed += e.bytesServed ?? 0;
        if (e.endReason) summary.lastEndReason = e.endReason;
        if (e.message) summary.lastEndMessage = e.message;
        summary.stallTotalsMs = {
          connWait: e.connWaitMs ?? 0,
          providerWait: e.providerWaitMs ?? 0,
          bodyDrain: e.bodyDrainMs ?? 0,
          consumerWait: e.consumerWaitMs ?? 0,
          clientWrite: e.clientWriteMs ?? 0,
        };
        break;
      case "Seek":
        summary.seeks++;
        break;
      case "Segment":
        summary.segments++;
        if (e.status) {
          summary.segmentsByStatus[e.status] = (summary.segmentsByStatus[e.status] ?? 0) + 1;
        }
        break;
      case "ZeroFill":
        summary.zeroFills++;
        break;
      case "Failover":
        summary.failovers++;
        break;
      case "Retry":
        summary.retries++;
        break;
      case "PrefetchWidth":
        summary.prefetchChanges++;
        break;
      case "StreamStartup":
        summary.startups++;
        if (e.status) summary.lastStartupPhase = describeStartupPhase(e.status);
        break;
    }
  }
  return summary;
}

/** Drop raw message-ids from copied output; keep enough to correlate counts. */
export function redactTraceEvents(events: StreamTraceEvent[]): StreamTraceEvent[] {
  return events.map((e) => (e.segmentId ? { ...e, segmentId: `${e.segmentId.slice(0, 12)}…` } : e));
}

export function describeTraceEvent(e: StreamTraceEvent): string {
  switch (e.kind) {
    case "RangeOpen":
      return `range ${e.rangeStart ?? 0}–${e.rangeEnd ?? "end"}`;
    case "RangeEnd":
      return `${e.endReason ?? "?"}${e.bytesServed != null ? ` · ${e.bytesServed} B` : ""}${e.message ? ` · ${e.message}` : ""}`;
    case "Seek":
      return `offset ${e.offset ?? "?"}`;
    case "Segment":
      return `${e.provider ?? "?"} ${e.status ?? "?"}${e.durationMs != null ? ` · ${e.durationMs} ms` : ""}${e.retries ? ` · ${e.retries} retries` : ""}`;
    case "ZeroFill":
      return `${e.bytes ?? "?"} B zero-filled${e.message ? ` · ${e.message}` : ""}`;
    case "Failover":
      return `${e.fromProvider ?? "?"} → ${e.toProvider ?? "?"}${e.message ? ` · ${e.message}` : ""}`;
    case "Retry":
      return `attempt ${e.attempt ?? "?"}${e.message ? ` · ${e.message}` : ""}`;
    case "PrefetchWidth":
      return `${e.previousBatchSize ?? "?"} → ${e.batchSize ?? "?"}`;
    case "StreamStartup":
      return `${describeStartupPhase(e.status)}${e.bytes != null ? ` · ${e.bytes} B` : ""}${e.durationMs != null ? ` · ${e.durationMs} ms` : ""}`;
    default:
      return e.message ?? e.kind;
  }
}

const STARTUP_PHASE_LABELS: Record<string, string> = {
  "exact-index-direct": "exact-index direct",
  "legacy-buffered": "legacy buffered seek",
  "legacy-probed-unbuffered": "legacy header-probed seek",
  "handoff-not-needed": "handoff not needed",
  "handoff-eager": "eager remainder handoff",
  "handoff-legacy-lazy": "lazy remainder handoff",
  "handoff-scheduled": "remainder scheduled",
  "handoff-activated": "remainder activated",
  "remainder-factory-failed": "remainder factory failed",
  "prefix-discard": "prefix discard",
  "remainder-wait": "remainder wait",
};

export function describeStartupPhase(status: string | null | undefined): string {
  if (!status) return "startup";
  return STARTUP_PHASE_LABELS[status] ?? status;
}
