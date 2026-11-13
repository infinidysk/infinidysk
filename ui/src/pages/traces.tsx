import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowLeft, ChevronDown, ChevronLeft, ChevronRight, Download, Square } from "lucide-react";
import { useMemo, useState } from "react";
import { Link, useParams } from "react-router";
import { api, body, type Schemas } from "../api/client";
import { ago, bytes, stamp } from "../format";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { Empty, Failure, IconButton, InfoTip, Loading, PageHeader, Panel, Status } from "../ui";

// The server sends these untyped (`summary`, JSON lines); they mirror `infinidysk_core::trace`.
type Cause = "budget" | "permit" | "connection" | "pipeline" | "latency" | "transfer" | "decode" | "disk" | "shared" | "other";
type Summary = {
  events: number;
  dropped: number;
  first_byte_ns: number | null;
  stalls: number;
  stall_ns: number;
  longest_stall_ns: number;
  causes: Record<Cause, number>;
  served: number;
  attempts: number;
  failed: number;
  hedges: number;
  hedges_won: number;
  queued_behind: number;
  drained_bytes: number;
  drained_ns: number;
  stall_idle_ns: number;
  cached: number;
  seeks: number;
  client_ns: number;
  // Absent in traces from older servers.
  open_before_ns?: number | null;
  open_reply_ns?: number | null;
  requests?: number;
  requests_failed?: number;
  requests_cancelled?: number;
  requests_pending?: number;
  requests_unmatched?: number;
  first_request_ns?: number | null;
  first_reply_ns?: number | null;
  request_order_ns?: number;
  request_lock_ns?: number;
  request_read_ns?: number;
  longest_order_ns?: number;
  longest_lock_ns?: number;
  longest_read_ns?: number;
  longest_request_ns?: number;
  in_request_ns?: number;
  no_request_ns?: number;
  longest_no_request_ns?: number;
};
type Outcome = "retrieved" | "not_found" | "cancelled" | { not_retrieved: string };
type SegmentStep =
  | { step: "queued" }
  | { step: "budgeted" }
  | { step: "bypassed" }
  | { step: "disk_read" }
  | { step: "joined"; flight: number }
  | { step: "permitted"; provider: number }
  | { step: "connected"; provider: number; ahead: number; connection: number }
  | { step: "reading"; provider: number }
  | { step: "receiving"; provider: number }
  | { step: "done"; provider: number; outcome: Outcome; bytes: number; decode_ns: number };
type TraceEvent = { at_ns: number } & (
  | { type: "open"; size: number }
  | { type: "seek"; from: number; to: number }
  | { type: "window" | "hedge" | "hedge_won" | "served"; segment: number }
  | { type: "cached"; segment: number; tier: "memory" | "disk" | "shared" }
  | ({ type: "segment"; segment: number; hedge?: boolean } & SegmentStep)
  | { type: "stall"; segment: number; ns: number }
  | { type: "idle"; ns: number }
  | {
      type: "sample";
      in_flight: number;
      ready: number;
      budget_free: number;
      permits_free: number;
      window: number;
      connections: number;
      idle_connections: number;
    }
  | { type: "end"; bytes: number }
  | { type: "dropped"; events: number }
);
type Header = {
  read: Schemas["ReadView"];
  providers: string[];
  summary: Summary;
  dropped: number;
  clock?: { started_unix_ns: number; exported_unix_ns: number; exported_at_ns: number };
  coverage?: string[];
};

const causes: { cause: Cause; label: string; hint: string; color: string }[] = [
  { cause: "budget", label: "Budget", hint: "Decoded-bytes budget held by other reads", color: "oklch(0.62 0.17 300)" },
  { cause: "permit", label: "Permit", hint: "All download permits in use", color: "oklch(0.74 0.15 75)" },
  { cause: "connection", label: "Connection", hint: "Waiting for a provider connection", color: "oklch(0.66 0.18 40)" },
  { cause: "pipeline", label: "Pipeline", hint: "Behind earlier replies on the connection", color: "oklch(0.66 0.11 190)" },
  { cause: "latency", label: "Latency", hint: "Waiting for the provider to answer", color: "oklch(0.62 0.17 250)" },
  { cause: "transfer", label: "Transfer", hint: "The reply arriving over the network", color: "oklch(0.7 0.13 220)" },
  { cause: "decode", label: "Decode", hint: "yEnc decode and CRC check", color: "oklch(0.68 0.16 145)" },
  { cause: "disk", label: "Disk", hint: "Reading the segment from the disk cache", color: "oklch(0.64 0.12 110)" },
  { cause: "shared", label: "Shared", hint: "Waiting for another read fetching the same segment", color: "oklch(0.66 0.14 350)" },
  { cause: "other", label: "Other", hint: "Before the segment was scheduled", color: "oklch(0.6 0 0)" },
];
const color = Object.fromEntries(causes.map((c) => [c.cause, c.color])) as Record<Cause, string>;

/** "12.3 ms" or "4.1 s". */
function ms(ns: number): string {
  const v = ns / 1e6;
  if (v >= 10_000) return `${(v / 1000).toFixed(1)} s`;
  return `${v.toFixed(v < 10 ? 1 : 0)} ms`;
}

const durations = [15, 30, 60];

/** The tracing switch and the kept traces, for the System page. */
export function Tracing() {
  const client = useQueryClient();
  const tracing = useQuery({
    queryKey: ["tracing"],
    queryFn: () => body(api.GET("/api/v1/tracing")),
    refetchInterval: 15_000,
  });
  const on = tracing.data?.until != null && tracing.data.until > Date.now();
  const traces = useQuery({
    queryKey: ["traces"],
    queryFn: () => body(api.GET("/api/v1/traces")),
    refetchInterval: (q) => (on || q.state.data?.some((t) => t.closed_at == null) ? 3000 : false),
  });
  const set = useMutation({
    mutationFn: (minutes: number) => body(api.PUT("/api/v1/tracing", { body: { minutes } })),
    onSuccess: (data) => client.setQueryData(["tracing"], data),
  });

  return (
    <section className="mb-6">
      <div className="mb-3 flex flex-wrap items-center gap-2">
        <div className="mr-auto">
          <h2 className="font-medium">Read traces</h2>
          <p className="text-muted-foreground text-xs">
            Records every segment fetch of new reads to explain stalls. Costs little; leave it off unless diagnosing.
          </p>
        </div>
        {on ? (
          <>
            <Status tone="primary">
              <span>Tracing until {new Date(tracing.data!.until!).toLocaleTimeString()}</span>
            </Status>
            <Button variant="outline" size="sm" disabled={set.isPending} onClick={() => set.mutate(0)}>
              <Square />
              Stop
            </Button>
          </>
        ) : (
          <div className="flex">
            <Button
              variant="outline"
              size="sm"
              className="rounded-r-none"
              disabled={set.isPending || tracing.isPending}
              onClick={() => set.mutate(durations[0]!)}
            >
              Trace {durations[0]} min
            </Button>
            <DropdownMenu>
              <DropdownMenuTrigger asChild>
                <Button
                  variant="outline"
                  size="icon-sm"
                  className="-ml-px rounded-l-none"
                  aria-label="Trace for longer"
                  disabled={set.isPending || tracing.isPending}
                >
                  <ChevronDown />
                </Button>
              </DropdownMenuTrigger>
              <DropdownMenuContent align="end">
                {durations.slice(1).map((m) => (
                  <DropdownMenuItem key={m} onSelect={() => set.mutate(m)}>
                    Trace {m} min
                  </DropdownMenuItem>
                ))}
              </DropdownMenuContent>
            </DropdownMenu>
          </div>
        )}
      </div>
      {set.isError && <Failure error={set.error} />}
      {traces.isPending ? (
        <Loading />
      ) : traces.isError ? (
        <Failure error={traces.error} />
      ) : traces.data.length === 0 ? (
        <Empty title="No traces">{on ? "Open a file through the mount or WebDAV to trace it." : "Start tracing, then play the file that stalls."}</Empty>
      ) : (
        <Panel>
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>File</TableHead>
                <TableHead className="hidden w-28 sm:table-cell">Opened</TableHead>
                <TableHead className="hidden w-24 text-right md:table-cell">Read</TableHead>
                <TableHead className="hidden w-24 text-right md:table-cell">
                  Stream first byte <InfoTip label="About stream first byte">From the first prefetch window to its first bytes.</InfoTip>
                </TableHead>
                <TableHead className="w-24 text-right">Stalls</TableHead>
                <TableHead className="hidden w-24 text-right lg:table-cell">Longest</TableHead>
                <TableHead className="w-12">
                  <span className="sr-only">Actions</span>
                </TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {traces.data.map((t) => {
                const s = t.summary as unknown as Summary;
                return (
                  <TableRow key={t.id}>
                    <TableCell className="w-full max-w-0">
                      <Link to={`/system/traces/${t.id}`} className="block truncate font-medium hover:underline" title={t.path}>
                        {t.path.split("/").pop()}
                      </Link>
                      <div className="text-muted-foreground text-xs whitespace-normal wrap-anywhere">
                        {t.delivery === "fuse" ? "FUSE" : "WebDAV"} · {t.client || "Unknown client"}
                        {t.closed_at == null && <span className="text-primary"> · open</span>}
                      </div>
                    </TableCell>
                    <TableCell className="text-muted-foreground hidden sm:table-cell" title={stamp(t.opened_at)}>
                      {ago(t.opened_at)}
                    </TableCell>
                    <TableCell className="hidden text-right tabular-nums md:table-cell">{bytes(t.served)}</TableCell>
                    <TableCell className="hidden text-right tabular-nums md:table-cell">
                      {s.first_byte_ns == null ? "–" : ms(s.first_byte_ns)}
                    </TableCell>
                    <TableCell className={cn("text-right tabular-nums", s.stalls > 0 && "text-warning-text")}>
                      {s.stalls > 0 ? `${s.stalls} · ${ms(s.stall_ns)}` : "0"}
                    </TableCell>
                    <TableCell className="hidden text-right tabular-nums lg:table-cell">{s.stalls > 0 ? ms(s.longest_stall_ns) : "–"}</TableCell>
                    <TableCell className="text-right">
                      <IconButton label="Export for a bug report" asChild>
                        <a href={`/api/v1/traces/${t.id}/export`} download={`trace-${t.id}.ndjson`}>
                          <Download />
                        </a>
                      </IconButton>
                    </TableCell>
                  </TableRow>
                );
              })}
            </TableBody>
          </Table>
        </Panel>
      )}
    </section>
  );
}

export function parse(text: string): { header: Header; events: TraceEvent[] } {
  const [first = "{}", ...rest] = text.split("\n").filter(Boolean);
  return { header: JSON.parse(first) as Header, events: rest.map((l) => JSON.parse(l) as TraceEvent) };
}

type Bar = { cause: Cause; start: number; end: number; provider?: number };
type Attempt = { provider: number; bars: Bar[]; outcome?: Outcome };
type Row = {
  segment: number;
  first: number;
  last: number;
  waits: Bar[];
  attempts: Attempt[];
  served?: number;
  stalls: { start: number; end: number }[];
};

/** One row per segment: its waits before an attempt, then each provider attempt's phases. */
function rows(events: TraceEvent[]): Row[] {
  const bySegment = new Map<number, Row>();
  const open = new Map<number, Bar>();
  const live = new Map<string, Attempt>();
  const row = (segment: number, at: number) => {
    let r = bySegment.get(segment);
    if (!r) {
      r = { segment, first: at, last: at, waits: [], attempts: [], stalls: [] };
      bySegment.set(segment, r);
    }
    r.last = Math.max(r.last, at);
    return r;
  };
  const close = (segment: number, at: number) => {
    const bar = open.get(segment);
    if (bar) {
      bar.end = at;
      row(segment, at).waits.push(bar);
      open.delete(segment);
    }
  };
  for (const e of events) {
    const at = e.at_ns;
    if (e.type === "served") row(e.segment, at).served = at;
    else if (e.type === "stall") row(e.segment, at).stalls.push({ start: at - e.ns, end: at });
    else if (e.type === "cached") close(e.segment, at);
    if (e.type !== "segment") continue;
    const r = row(e.segment, at);
    if (e.step === "queued" || e.step === "budgeted" || e.step === "bypassed" || e.step === "disk_read" || e.step === "joined") {
      close(e.segment, at);
      const cause: Cause = e.step === "queued" ? "budget" : e.step === "disk_read" ? "disk" : e.step === "joined" ? "shared" : "permit";
      open.set(e.segment, { cause, start: at, end: at });
      continue;
    }
    const key = `${e.segment}:${e.provider}:${e.hedge ? "h" : ""}`;
    let attempt = live.get(key);
    if (e.step === "permitted" || !attempt) {
      close(e.segment, at);
      attempt = { provider: e.provider, bars: [] };
      r.attempts.push(attempt);
      live.set(key, attempt);
    }
    const last = attempt.bars.at(-1);
    if (last) last.end = at;
    if (e.step === "done") {
      // The tail of the transfer bar was decoding.
      if (last?.cause === "transfer" && e.decode_ns > 0) {
        const split = Math.max(last.start, at - e.decode_ns);
        last.end = split;
        attempt.bars.push({ cause: "decode", start: split, end: at, provider: e.provider });
      }
      attempt.outcome = e.outcome;
      live.delete(key);
      // A failed attempt waits for the next provider's permit.
      if (e.outcome !== "retrieved" && e.outcome !== "cancelled") open.set(e.segment, { cause: "permit", start: at, end: at });
    } else {
      const cause: Cause =
        e.step === "permitted" ? "connection" : e.step === "connected" ? "pipeline" : e.step === "reading" ? "latency" : "transfer";
      attempt.bars.push({ cause, start: at, end: at, provider: e.provider });
    }
  }
  const end = events.at(-1)?.at_ns ?? 0;
  // Waits still open at the end of the trace run to its end.
  for (const [segment, bar] of open) {
    bar.end = end;
    row(segment, end).waits.push(bar);
  }
  for (const a of live.values()) {
    const last = a.bars.at(-1);
    if (last) last.end = end;
  }
  return [...bySegment.values()].sort((a, b) => a.first - b.first || a.segment - b.segment);
}

const ROWS = 500;

export function TracePage() {
  const { id } = useParams();
  const trace = useQuery({
    queryKey: ["trace", id],
    queryFn: async () =>
      parse(await body(api.GET("/api/v1/traces/{id}", { params: { path: { id: Number(id) } }, parseAs: "text" }))),
  });
  const read = trace.data?.header.read;

  return (
    <>
      <Link to="/system" className="text-muted-foreground hover:text-foreground mb-3 inline-flex items-center gap-1 text-xs">
        <ArrowLeft className="size-3.5" />
        System
      </Link>
      <PageHeader
        title={read ? (read.path.split("/").pop() ?? read.path) : "Trace"}
        description={read?.path}
        tab={[read?.path.split("/").pop(), "Trace", "System"]}
      >
        <Button variant="outline" size="sm" asChild>
          <a href={`/api/v1/traces/${id}`} download={`trace-${id}.jsonl`}>
            <Download />
            JSON lines
          </a>
        </Button>
        <Button variant="outline" size="sm" asChild title="File name and provider names redacted, version and tuning included">
          <a href={`/api/v1/traces/${id}/export`} download={`trace-${id}.ndjson`}>
            <Download />
            Export for bug report
          </a>
        </Button>
      </PageHeader>
      {trace.isPending ? <Loading /> : trace.isError ? <Failure error={trace.error} /> : <TraceBody {...trace.data} />}
    </>
  );
}

export function TraceBody({ header, events }: { header: Header; events: TraceEvent[] }) {
  const { read, summary: s, providers, coverage = [] } = header;
  const all = useMemo(() => rows(events), [events]);
  const [page, setPage] = useState(0);
  const shown = all.slice(page * ROWS, (page + 1) * ROWS);
  const pages = Math.ceil(all.length / ROWS);
  const opt = (ns: number | null | undefined) => (ns == null ? "–" : ms(ns));
  const wait = (total = 0, longest = 0) => (total > 0 ? `${ms(total)} · longest ${ms(longest)}` : "–");
  const requests = s.requests ?? 0;
  const stats: [string, string, boolean?, string?][] = [
    ["Client", `${read.delivery === "fuse" ? "FUSE" : "WebDAV"} · ${read.client || "unknown"}`],
    ["Read", `${bytes(read.served)} of ${bytes(read.size)}`],
    ["Stream first byte", opt(s.first_byte_ns), false, "From the reader's first prefetch window to its first bytes. Not click-to-play."],
    ["Stalls", s.stalls > 0 ? `${s.stalls} · ${ms(s.stall_ns)}` : "none", s.stalls > 0],
    ["Longest stall", s.stalls > 0 ? ms(s.longest_stall_ns) : "–"],
    ["Segments served", s.cached > 0 ? `${s.served} · ${s.cached} cached` : String(s.served)],
    ["Attempts", s.failed > 0 ? `${s.attempts} · ${s.failed} failed` : String(s.attempts), s.failed > 0],
    ["Hedges won · seeks", `${s.hedges_won} of ${s.hedges} · ${s.seeks}`],
    ["Drained", s.drained_bytes > 0 ? `${bytes(s.drained_bytes)} · ${ms(s.drained_ns)}` : "–"],
    [
      "Between stream reads",
      s.client_ns > 0 ? ms(s.client_ns) : "–",
      false,
      "Gaps of 50 ms or more between reads of the stream. Includes request ordering and handle-lock waits; not proof the player paused.",
    ],
  ];
  if (s.open_before_ns != null) {
    stats.push(
      ["Open before stream", opt(s.open_before_ns), false, "Open work before the stream started: path, caller and metadata lookups."],
      ["Open reply", opt(s.open_reply_ns), false, "From the stream start to the open's reply."],
    );
  }
  if (requests > 0) {
    const failed = s.requests_failed ?? 0;
    const cancelled = s.requests_cancelled ?? 0;
    const pending = s.requests_pending ?? 0;
    stats.push(
      [
        "FUSE requests",
        [`${requests}`, failed > 0 && `${failed} failed`, cancelled > 0 && `${cancelled} cancelled`, pending > 0 && `${pending} pending`]
          .filter(Boolean)
          .join(" · "),
        failed + pending > 0,
      ],
      ["First request", opt(s.first_request_ns), false, "From the stream start to the first read request reaching infinidysk."],
      ["First reply", opt(s.first_reply_ns), false, "That first request's time in infinidysk, from arrival to reply."],
      ["Ordering wait", wait(s.request_order_ns, s.longest_order_ns), false, "Requests waiting for earlier reads of the same handle. Summed; requests overlap."],
      ["Handle lock wait", wait(s.request_lock_ns, s.longest_lock_ns), false, "Requests waiting for the handle's reader, busy with another request."],
      ["In stream reader", wait(s.request_read_ns, s.longest_read_ns), false, "Requests in the stream reader, including segment waits."],
      [
        "No request in infinidysk",
        wait(s.no_request_ns, s.longest_no_request_ns),
        false,
        "After the first request, time with no read request in infinidysk. The kernel, its cache or the player had it; the trace cannot tell which.",
      ],
    );
  }
  const samples = events.filter((e) => e.type === "sample");

  return (
    <>
      {s.dropped > 0 && (
        <p className="text-warning-text mb-3 text-xs">
          {s.dropped.toLocaleString()} events from the middle of the read were dropped; totals cover only what was kept.
        </p>
      )}
      <dl className="bg-card mb-6 grid grid-cols-2 gap-px overflow-hidden rounded-lg border sm:grid-cols-4">
        {stats.map(([label, value, warn, hint]) => (
          <div key={label} className="bg-card p-3 outline outline-border">
            <dt className="text-muted-foreground flex items-center gap-1 text-xs">
              {label}
              {hint && <InfoTip label={`About ${label.toLowerCase()}`}>{hint}</InfoTip>}
            </dt>
            <dd className={cn("mt-0.5 truncate font-medium tabular-nums", warn && "text-warning-text")}>{value}</dd>
          </div>
        ))}
      </dl>
      {coverage.length > 0 && (
        <ul className="text-muted-foreground mb-6 list-disc space-y-1 pl-5 text-xs">
          {coverage.map((note) => (
            <li key={note}>{note}</li>
          ))}
        </ul>
      )}
      {s.stall_ns > 0 && <Attribution summary={s} />}
      {samples.length > 0 && <Samples samples={samples} />}
      <section>
        <div className="mb-3 flex flex-wrap items-center gap-3">
          <h2 className="mr-auto font-medium">Segments</h2>
          <Legend />
        </div>
        {all.length === 0 ? (
          <Empty title="No segment fetches">The read ended before any segment was requested.</Empty>
        ) : (
          <>
            <Waterfall rows={shown} providers={providers} />
            {pages > 1 && (
              <div className="mt-4 flex items-center justify-end gap-2">
                <span className="text-muted-foreground mr-2 tabular-nums">
                  Page {page + 1} of {pages}
                </span>
                <Button variant="outline" size="sm" disabled={page === 0} onClick={() => setPage(page - 1)}>
                  <ChevronLeft />
                  Previous
                </Button>
                <Button variant="outline" size="sm" disabled={page + 1 >= pages} onClick={() => setPage(page + 1)}>
                  Next
                  <ChevronRight />
                </Button>
              </div>
            )}
          </>
        )}
      </section>
    </>
  );
}

function Attribution({ summary }: { summary: Summary }) {
  return (
    <section className="mb-6">
      <h2 className="mb-1 font-medium">What the reader waited on</h2>
      <p className="text-muted-foreground mb-3 text-xs">Stall time charged to the phase each late segment was in.</p>
      <div className="bg-muted flex h-3 overflow-hidden rounded-full">
        {causes.map(({ cause, label }) =>
          summary.causes[cause] > 0 ? (
            <div
              key={cause}
              style={{ width: `${(summary.causes[cause] / summary.stall_ns) * 100}%`, background: color[cause] }}
              title={`${label}: ${ms(summary.causes[cause])}`}
            />
          ) : null,
        )}
      </div>
      <ul className="mt-3 grid grid-cols-2 gap-x-6 gap-y-1.5 sm:grid-cols-3">
        {causes
          .filter(({ cause }) => summary.causes[cause] > 0)
          .map(({ cause, label, hint }) => (
            <li key={cause} className="flex items-baseline gap-2" title={hint}>
              <span className="size-2 shrink-0 rounded-full" style={{ background: color[cause] }} aria-hidden />
              <span>{label}</span>
              <span className="text-muted-foreground ml-auto tabular-nums">
                {ms(summary.causes[cause])} · {Math.round((summary.causes[cause] / summary.stall_ns) * 100)}%
              </span>
            </li>
          ))}
      </ul>
    </section>
  );
}

type Sample = Extract<TraceEvent, { type: "sample" }>;

/** Read-ahead state once a second: how far ahead the read was and what was free. */
function Samples({ samples }: { samples: Sample[] }) {
  const peak = Math.max(1, ...samples.map((x) => x.in_flight + x.ready));
  const low = samples.reduce((a, b) => (b.ready < a.ready ? b : a));
  return (
    <section className="mb-6">
      <h2 className="mb-1 font-medium">Read-ahead</h2>
      <p className="text-muted-foreground mb-3 text-xs">
        Segments ready ahead of the reader (solid) and still downloading (faint), sampled each second. Fewest ready: {low.ready} at{" "}
        {ms(low.at_ns)}, with {low.permits_free} permits and {bytes(low.budget_free)} budget free.
      </p>
      <div className="bg-card flex h-16 items-end gap-px overflow-hidden rounded-lg border p-1">
        {samples.map((x) => (
          <div
            key={x.at_ns}
            className="flex min-w-px flex-1 flex-col justify-end"
            title={`${ms(x.at_ns)}: ${x.ready} ready, ${x.in_flight} downloading of a ${x.window}-segment window, ${x.idle_connections} of ${x.connections} connections idle, ${x.permits_free} permits free, ${bytes(x.budget_free)} budget free`}
          >
            <div className="bg-primary/30" style={{ height: `${(x.in_flight / peak) * 100}%` }} />
            <div className={cn(x.ready === 0 ? "bg-destructive" : "bg-primary")} style={{ height: `${Math.max(x.ready === 0 ? 4 : 0, (x.ready / peak) * 100)}%` }} />
          </div>
        ))}
      </div>
    </section>
  );
}

function Legend() {
  return (
    <ul className="text-muted-foreground flex flex-wrap gap-x-3 gap-y-1 text-xs">
      {causes
        .filter((c) => c.cause !== "other")
        .map(({ cause, label, hint }) => (
          <li key={cause} className="inline-flex items-center gap-1.5" title={hint}>
            <span className="h-2 w-3 rounded-sm" style={{ background: color[cause] }} aria-hidden />
            {label}
          </li>
        ))}
      <li className="inline-flex items-center gap-1.5">
        <span className="bg-destructive/25 h-2 w-3 rounded-sm" aria-hidden />
        Stall
      </li>
      <li className="inline-flex items-center gap-1.5">
        <span className="bg-foreground h-2 w-0.5" aria-hidden />
        Served
      </li>
    </ul>
  );
}

function outcomeLabel(outcome?: Outcome): string {
  if (outcome === undefined) return "unfinished";
  if (typeof outcome === "string") return outcome.replace("_", " ");
  return `failed (${outcome.not_retrieved})`;
}

function Waterfall({ rows, providers }: { rows: Row[]; providers: string[] }) {
  const start = Math.min(...rows.map((r) => r.first));
  const end = Math.max(...rows.map((r) => Math.max(r.last, ...r.stalls.map((s) => s.end))));
  const span = Math.max(end - start, 1);
  const pos = (a: number, b: number) => ({
    left: `${((a - start) / span) * 100}%`,
    width: `max(1px, ${((b - a) / span) * 100}%)`,
  });
  const name = (p: number) => providers[p] ?? `provider ${p}`;

  return (
    <Panel className="font-mono text-[11px]">
      <div className="text-muted-foreground flex border-b px-3 py-1.5">
        <span className="w-16 shrink-0">Segment</span>
        <div className="flex flex-1 justify-between tabular-nums">
          <span>{ms(start)}</span>
          <span>{ms(start + span / 2)}</span>
          <span>{ms(end)}</span>
        </div>
      </div>
      <ul className="divide-y">
        {rows.map((r) => (
          <li key={r.segment} className="hover:bg-muted/40 flex items-center px-3 py-0.5">
            <span className="text-muted-foreground w-16 shrink-0 tabular-nums">{r.segment}</span>
            <div className="relative flex-1" style={{ height: `${Math.max(1, r.attempts.length) * 6 + 4}px` }}>
              {r.stalls.map((st, i) => (
                <div
                  key={`s${i}`}
                  className="bg-destructive/25 absolute inset-y-0"
                  style={pos(st.start, st.end)}
                  title={`Reader stalled ${ms(st.end - st.start)}`}
                />
              ))}
              {r.waits.map((b, i) => (
                <div
                  key={`w${i}`}
                  className="absolute top-0.5 h-1 rounded-sm"
                  style={{ ...pos(b.start, b.end), background: color[b.cause] }}
                  title={`${b.cause} ${ms(b.end - b.start)}`}
                />
              ))}
              {r.attempts.map((a, j) =>
                a.bars.map((b, i) => (
                  <div
                    key={`a${j}-${i}`}
                    className={cn("absolute h-1 rounded-sm", a.outcome !== undefined && a.outcome !== "retrieved" && "opacity-50")}
                    style={{ ...pos(b.start, b.end), top: `${j * 6 + 2}px`, background: color[b.cause] }}
                    title={`${name(a.provider)} · ${b.cause} ${ms(b.end - b.start)} · ${outcomeLabel(a.outcome)}`}
                  />
                )),
              )}
              {r.served !== undefined && (
                <div className="bg-foreground absolute inset-y-0 w-0.5" style={{ left: pos(r.served, r.served).left }} title={`Served at ${ms(r.served)}`} />
              )}
            </div>
          </li>
        ))}
      </ul>
    </Panel>
  );
}
