import { useQuery } from "@tanstack/react-query";
import { ArrowDown, ArrowUp, Settings } from "lucide-react";
import { memo, useState } from "react";
import { Link } from "react-router";
import { api, body, type Schemas } from "../api/client";
import { ago, bytes, stamp } from "../format";
import { cn } from "@/lib/utils";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { Failure, InfoTip, Loading, PageHeader, ReleaseName, Status } from "../ui";
import {
  Card,
  type History,
  type Live,
  Spark,
  Tiles,
  type Window,
  WindowPicker,
  WriterError,
  bucketTime,
  count,
  displayName,
  ms,
  name,
  pct,
  rate,
  series,
  spans,
  useHistory,
} from "./overview";

type Score = Schemas["ProviderScore"];

export function Stats() {
  const [span, setSpan] = useState<Window>("24h");
  // Kept fresh by the `live` server-sent event; only the provider table reads it.
  const live = useQuery({
    queryKey: ["live"],
    queryFn: () => body(api.GET("/api/v1/live")),
    staleTime: Infinity,
  });
  const history = useHistory(span);
  const error = live.data?.writer_error ?? history.data?.writer_error;
  const h = history.data;
  const quiet = h && h.totals.requests === 0 && h.top_reads.length === 0 && h.apps.length === 0;

  return (
    <>
      <PageHeader title="Stats" description={`Providers, workloads, rescues and reads, ${spans[span]}`}>
        <WindowPicker value={span} onChange={setSpan} />
      </PageHeader>
      <WriterError error={error} />
      {history.isPending ? (
        <Loading />
      ) : history.isError ? (
        <Failure error={history.error} />
      ) : (
        <div className="space-y-6">
          {quiet ? (
            <p className="text-muted-foreground rounded-lg border border-dashed px-6 py-8 text-center">
              No reads or provider requests {span === "all" ? "yet" : `in the ${spans[span]}`}.
            </p>
          ) : (
            <>
              <Providers history={history.data} live={live.data} span={span} />
              <Workloads history={history.data} span={span} />
              <Rescues history={history.data} span={span} />
              <TopReads history={history.data} span={span} />
              <Apps history={history.data} span={span} />
            </>
          )}
          <Catalogue history={history.data} />
          <Lifetime history={history.data} />
        </div>
      )}
    </>
  );
}

/** Share of each bucket a provider's circuit breaker was open. */
function outages(provider: string, h: History) {
  const bucket = h.bucket_ms;
  return h.points.map((p) => {
    let open = 0;
    for (const e of h.breaker_events) {
      if (e.provider !== provider) continue;
      const from = Math.max(e.at, p.ts);
      const to = Math.min(e.at + Math.max(e.open_ms, bucket / 50), p.ts + bucket);
      if (to > from) open += to - from;
    }
    return Math.min(1, open / bucket);
  });
}

function Providers({ history: h, live, span }: { history: History; live?: Live; span: Window }) {
  const now = new Map(live?.providers.map((p) => [p.key, p]));
  const scores = new Map(h.providers.map((p) => [p.key, p]));
  const keys = [...new Set([...(live?.providers.map((p) => p.key) ?? []), ...h.providers.map((p) => p.key)])].sort(
    (a, b) => (now.get(a)?.priority ?? Number.MAX_SAFE_INTEGER) - (now.get(b)?.priority ?? Number.MAX_SAFE_INTEGER),
  );
  if (keys.length === 0) return null;
  const first = Math.min(...(live?.providers.map((p) => p.priority) ?? [0]));
  const total = h.providers.reduce((s, p) => s + p.attempts, 0);
  if (total === 0) return null;
  const several = h.providers.length > 1;
  const secs = h.bucket_ms / 1000;
  return (
    <Card
      title="Providers"
      description={`Requests to each provider, ${spans[span]}${h.totals.trips > 0 ? ` · ${count(h.totals.trips, "circuit trip")}` : ""}`}
      action={
        <Button variant="ghost" size="sm" asChild>
          <Link to="/settings/providers">
            <Settings />
            Settings
          </Link>
        </Button>
      }
    >
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>Provider</TableHead>
            <TableHead className="hidden 2xl:table-cell">Outages</TableHead>
            <TableHead className="text-right">Requests</TableHead>
            <TableHead className="hidden xl:table-cell">Rate</TableHead>
            <TableHead className="hidden md:table-cell">Errors</TableHead>
            <TableHead className="hidden md:table-cell">
              <span className="inline-flex items-center gap-1">
                Missing
                <InfoTip label="About missing">
                  Articles the provider did not have. A backup is asked only for what the providers before it missed, so a
                  high share there is expected.
                </InfoTip>
              </span>
            </TableHead>
            <TableHead className="text-right">
              <span className="inline-flex items-center gap-1">
                Latency
                <InfoTip label="About latency">Playback request time: median, then the 95th and 99th percentile.</InfoTip>
              </span>
            </TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {keys.map((key) => {
            const n = now.get(key);
            const s: Score | undefined = scores.get(key);
            const name = n?.name ?? s?.name ?? key;
            const points = s?.points ?? [];
            const failures = s ? (s.failures > 0 ? `${count(s.failures)} · ${pct(s.failures, s.attempts)}` : "0") : "–";
            const missing = s ? (s.missing > 0 ? `${count(s.missing)} · ${pct(s.missing, s.attempts)}` : "0") : "–";
            return (
              <TableRow key={key}>
                <TableCell className="w-full max-w-0">
                  <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
                    <Status
                      tone={!n ? "muted" : n.open_for_ms != null ? "destructive" : n.live > 0 ? "success" : "muted"}
                      className="max-w-full min-w-0"
                    >
                      <span className={cn("truncate font-medium", !n && "text-muted-foreground")} title={name}>
                        {name}
                      </span>
                    </Status>
                    {n && n.priority > first && <Badge variant="secondary">Backup</Badge>}
                    {!n && <Badge variant="outline">Removed</Badge>}
                    {n?.open_for_ms != null && <span className="text-destructive text-xs">paused {Math.ceil(n.open_for_ms / 1000)} s</span>}
                  </div>
                  {s && (s.failures > 0 || s.missing > 0 || s.trips > 0) && (
                    <div className="text-muted-foreground mt-0.5 font-mono text-xs whitespace-normal tabular-nums md:hidden">
                      <span className={cn(s.failures > 0 && "text-destructive")}>{count(s.failures, "error")}</span> ·{" "}
                      <span className={cn(s.missing > 0 && "text-warning-text")}>{count(s.missing)} missing</span>
                      {s.trips > 0 && ` · ${count(s.trips, "trip")}`}
                    </div>
                  )}
                </TableCell>
                <TableCell className="hidden 2xl:table-cell">
                  <Spark values={outages(name, h)} max={1} color="var(--color-destructive)" bars />
                </TableCell>
                <TableCell className="text-right font-mono tabular-nums">
                  {s ? count(s.attempts) : "–"}
                  {s && (
                    <div className="text-muted-foreground text-xs">
                      {bytes(s.bytes)}
                      {several && <span className="hidden sm:inline"> · {pct(s.attempts, total)}</span>}
                    </div>
                  )}
                </TableCell>
                <TableCell className="hidden xl:table-cell">
                  <Spark values={points.map((p) => p.bytes / secs)} color="var(--color-chart-imports)" />
                  <div className="text-muted-foreground mt-1 grid grid-cols-[auto_auto] justify-between gap-x-3 font-mono text-xs tabular-nums">
                    <span>Peak</span>
                    <span>{s ? rate(s.peak_bps) : "–"}</span>
                    <span className="inline-flex items-center gap-1">
                      Busy avg
                      <InfoTip label="About busy average">Average rate while receiving.</InfoTip>
                    </span>
                    <span>{s && s.active_ms > 0 ? rate(s.bytes / (s.active_ms / 1000)) : "–"}</span>
                  </div>
                </TableCell>
                <TableCell className="hidden md:table-cell">
                  <Spark values={points.map((p) => p.failures)} color="var(--color-destructive)" />
                  <div className={cn("font-mono text-xs tabular-nums", s && s.failures > 0 ? "text-destructive" : "text-muted-foreground")}>{failures}</div>
                  {s && s.retried > 0 && (
                    <div className="text-muted-foreground flex items-center gap-1 font-mono text-xs tabular-nums">
                      {count(s.retried)} recovered
                      <InfoTip label="About recovered">Failed attempts that a retry or another provider recovered.</InfoTip>
                    </div>
                  )}
                  {s && (s.trips > 0 || s.connect_failures > 0) && (
                    <div className="text-muted-foreground flex items-center gap-1 font-mono text-xs tabular-nums">
                      {count(s.trips, "trip")} · {count(s.connect_failures)} connect
                      <InfoTip label="About trips">Circuit breaker openings, and connections that could not be opened.</InfoTip>
                    </div>
                  )}
                </TableCell>
                <TableCell className="hidden md:table-cell">
                  <Spark values={points.map((p) => p.missing)} color="var(--color-warning)" />
                  <div className={cn("font-mono text-xs tabular-nums", s && s.missing > 0 ? "text-warning-text" : "text-muted-foreground")}>{missing}</div>
                </TableCell>
                <TableCell className="text-right font-mono tabular-nums">
                  {latency(s?.p50_us)}
                  {s?.p95_us != null && (
                    <div className="text-muted-foreground text-xs">
                      p95 {latency(s.p95_us)}
                      <br />
                      p99 {latency(s.p99_us)}
                    </div>
                  )}
                </TableCell>
              </TableRow>
            );
          })}
        </TableBody>
      </Table>
    </Card>
  );
}

const latency = (us?: number | null) => (us == null ? "–" : ms(Math.round(us / 1000)));

const workloadSeries = Object.fromEntries(series.map((s) => [s.key, s]));

const Workloads = memo(function Workloads({ history: h, span }: { history: History; span: Window }) {
  const rows = h.workloads;
  const total = rows.reduce((n, w) => n + w.bytes, 0);
  if (rows.every((w) => w.requests === 0 && w.bytes === 0)) return null;
  return (
    <Card title="By workload" description={`What playback, imports and health checks took from providers, ${spans[span]}`}>
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>Workload</TableHead>
            <TableHead className="text-right">Downloaded</TableHead>
            <TableHead className="hidden text-right sm:table-cell">Requests</TableHead>
            <TableHead className="hidden text-right md:table-cell">Errors</TableHead>
            <TableHead className="hidden text-right md:table-cell">Missing</TableHead>
            <TableHead className="text-right">
              <span className="inline-flex items-center gap-1">
                Latency
                <InfoTip label="About latency">Request time: median, then the 95th percentile.</InfoTip>
              </span>
            </TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {rows.map((w) => {
            const s = workloadSeries[w.workload];
            return (
              <TableRow key={w.workload}>
                <TableCell className="whitespace-normal">
                  <span className="flex items-center gap-2 font-medium">
                    <span className="size-2 rounded-full" style={{ background: s?.color }} aria-hidden />
                    {s?.label ?? w.workload}
                  </span>
                </TableCell>
                <TableCell className="text-right font-mono tabular-nums">
                  {bytes(w.bytes)}
                  <div className="text-muted-foreground text-xs">{pct(w.bytes, total)}</div>
                </TableCell>
                <TableCell className="hidden text-right font-mono tabular-nums sm:table-cell">{count(w.requests)}</TableCell>
                <TableCell className={cn("hidden text-right font-mono tabular-nums md:table-cell", w.failures > 0 && "text-destructive")}>
                  {w.failures > 0 ? `${count(w.failures)} · ${pct(w.failures, w.requests)}` : "0"}
                </TableCell>
                <TableCell className={cn("hidden text-right font-mono tabular-nums md:table-cell", w.missing > 0 && "text-warning-text")}>
                  {w.missing > 0 ? `${count(w.missing)} · ${pct(w.missing, w.requests)}` : "0"}
                </TableCell>
                <TableCell className="text-right font-mono tabular-nums">
                  {latency(w.p50_us)}
                  {w.p95_us != null && <div className="text-muted-foreground text-xs">p95 {latency(w.p95_us)}</div>}
                </TableCell>
              </TableRow>
            );
          })}
        </TableBody>
      </Table>
    </Card>
  );
});

const TopReads = memo(function TopReads({ history: h, span }: { history: History; span: Window }) {
  const top = h.top_reads;
  if (top.length === 0) return null;
  const most = top[0]!.served;
  return (
    <Card title="Most read files" description={`By bytes read, ${span === "all" ? "last 90 days" : spans[span]}`}>
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>File</TableHead>
            <TableHead className="hidden w-40 md:table-cell">
              <span className="sr-only">Share</span>
            </TableHead>
            <TableHead className="text-right">Read</TableHead>
            <TableHead className="text-right">Opens</TableHead>
            <TableHead className="hidden text-right sm:table-cell">Last read</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {top.map((r) => (
            <TableRow key={r.item_id}>
              <TableCell className="w-full max-w-0">
                <Link
                  to={`/explorer?${new URLSearchParams({ item: r.item_id, search: name(r.path) })}`}
                  className="block truncate font-medium hover:underline"
                  title={r.path}
                >
                  <ReleaseName name={displayName(r.path)} />
                </Link>
              </TableCell>
              <TableCell className="hidden md:table-cell">
                <div className="bg-muted h-1.5 overflow-hidden rounded-full">
                  <div className="bg-primary h-full rounded-full" style={{ width: `${most > 0 ? (r.served / most) * 100 : 0}%` }} />
                </div>
              </TableCell>
              <TableCell className="text-right font-mono tabular-nums">{bytes(r.served)}</TableCell>
              <TableCell className="text-right font-mono tabular-nums">{count(r.reads)}</TableCell>
              <TableCell className="text-muted-foreground hidden text-right sm:table-cell" title={stamp(r.last_read_at)}>
                {ago(r.last_read_at)}
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </Card>
  );
});

const Apps = memo(function Apps({ history: h, span }: { history: History; span: Window }) {
  const apps = h.apps;
  if (apps.length === 0) return null;
  const most = apps[0]!.served;
  return (
    <Card title="By app" description={`Who read files, by bytes read, ${span === "all" ? "last 90 days" : spans[span]}`}>
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>App</TableHead>
            <TableHead className="hidden w-40 md:table-cell">
              <span className="sr-only">Share</span>
            </TableHead>
            <TableHead className="text-right">Read</TableHead>
            <TableHead className="text-right">Opens</TableHead>
            <TableHead className="hidden text-right sm:table-cell">Time open</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {apps.map((a) => (
            <TableRow key={`${a.delivery}-${a.app}`}>
              <TableCell className="w-full max-w-0">
                <div className="flex min-w-0 items-center gap-2">
                  <span className="truncate font-medium" title={a.app}>
                    {a.app || "Unknown"}
                  </span>
                  <Badge variant="outline" className="shrink-0">
                    {a.delivery === "fuse" ? "FUSE" : "WebDAV"}
                  </Badge>
                </div>
              </TableCell>
              <TableCell className="hidden md:table-cell">
                <div className="bg-muted h-1.5 overflow-hidden rounded-full">
                  <div className="bg-primary h-full rounded-full" style={{ width: `${most > 0 ? (a.served / most) * 100 : 0}%` }} />
                </div>
              </TableCell>
              <TableCell className="text-right font-mono tabular-nums">{bytes(a.served)}</TableCell>
              <TableCell className="text-right font-mono tabular-nums">{count(a.reads)}</TableCell>
              <TableCell className="text-muted-foreground hidden text-right sm:table-cell">{readTime(a.open_ms)}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </Card>
  );
});

function tally(items: { key: string; count: number }[]) {
  const by = new Map<string, number>();
  for (const { key, count } of items) by.set(key, (by.get(key) ?? 0) + count);
  return [...by].sort((a, b) => b[1] - a[1]);
}

/** How the first provider's attempt ended, as the server labels it. */
const reasonLabels: Record<string, string> = {
  not_found: "Not found",
  cancelled: "Cancelled",
  timeout: "Timed out",
  auth: "Sign-in failed",
  network: "Network error",
  protocol: "Protocol error",
  corrupt: "Corrupt",
  other: "Other",
};
const reasonLabel = (r: string) => reasonLabels[r] ?? r.charAt(0).toUpperCase() + r.slice(1).replaceAll("_", " ");

const Rescues = memo(function Rescues({ history: h, span }: { history: History; span: Window }) {
  const t = h.totals;
  if (h.providers.length < 2 && t.rescues === 0) return null;
  const saves = tally(h.rescues.map((r) => ({ key: r.to, count: r.count })));
  const misses = tally(h.rescues.map((r) => ({ key: r.from, count: r.count })));
  const reasons = tally(h.rescues.map((r) => ({ key: reasonLabel(r.reason), count: r.count })));
  const busiest = h.points.reduce<History["points"][number] | undefined>((best, p) => (p.rescues > (best?.rescues ?? 0) ? p : best), undefined);
  const prev = h.previous_rescues;
  const change = prev == null || prev === 0 ? null : Math.round(((t.rescues - prev) / prev) * 100);
  const reasonColors = ["var(--color-warning)", "var(--color-destructive)", "var(--color-chart-imports)", "var(--color-primary)", "var(--color-muted-foreground)"];

  return (
    <Card
      title="Backup rescues"
      description={`Segments one provider could not deliver and another did, before playback noticed (${spans[span]})`}
      action={
        change != null && (
          <div className="text-right">
            <div className="inline-flex items-center gap-1 font-mono text-sm tabular-nums">
              {change > 0 ? <ArrowUp className="size-3.5" aria-label="Up" /> : <ArrowDown className="size-3.5" aria-label="Down" />}
              {Math.abs(change)}%
            </div>
            <div className="text-muted-foreground text-xs">vs previous</div>
          </div>
        )
      }
    >
      <div className="flex items-baseline gap-4">
        <span className="font-mono text-lg font-medium tabular-nums">{count(t.rescues)}</span>
        <div>
          <div>segments rescued by another provider</div>
          <div className="text-muted-foreground text-xs">
            {pct(t.rescues, t.requests)} of all requests needed a second provider
          </div>
        </div>
      </div>
      {t.rescues > 0 && (
        <>
          <div className="mt-5 grid gap-3 sm:grid-cols-2">
            {(
              [
                ["Most saves", saves[0], "save", "saves", ""],
                ["Most misses", misses[0], "miss", "misses", "text-warning-text"],
              ] as const
            ).map(([label, top, one, many, tone]) => {
              const [who, n] = top ?? ["–", 0];
              return (
                <div key={label} className="bg-muted/50 min-w-0 rounded-md p-3">
                  <div className="text-muted-foreground text-xs">{label}</div>
                  <div className="mt-0.5 truncate font-medium" title={who}>
                    {who}
                  </div>
                  <div className="mt-2 font-mono text-sm">
                    <span className={tone}>{count(n)}</span> <span className="text-muted-foreground">{n === 1 ? one : many}</span>
                  </div>
                </div>
              );
            })}
          </div>
          <div className="mt-5">
            <div className="text-muted-foreground mb-2 text-xs">Why they missed</div>
            <div className="bg-muted flex h-2 overflow-hidden rounded-full">
              {reasons.map(([reason, n], i) => (
                <div key={reason} style={{ width: `${(n / t.rescues) * 100}%`, background: reasonColors[i % reasonColors.length] }} title={`${reason}: ${count(n)}`} />
              ))}
            </div>
            <div className="mt-2 flex flex-wrap gap-x-4 gap-y-1 text-xs">
              {reasons.map(([reason, n], i) => (
                <span key={reason} className="inline-flex items-center gap-1.5">
                  <span className="size-2 rounded-full" style={{ background: reasonColors[i % reasonColors.length] }} aria-hidden />
                  {reason} <span className="text-muted-foreground font-mono tabular-nums">{count(n)}</span>
                </span>
              ))}
            </div>
          </div>
          <div className="mt-5">
            <div className="text-muted-foreground mb-1 flex justify-between border-b pb-2 text-xs">
              <span>Needed rescuing</span>
              <span>Misses</span>
            </div>
            {misses.map(([who, n]) => (
              <div key={who} className="flex items-center gap-4 border-b py-2 last:border-0">
                <span className="w-40 truncate" title={who}>
                  {who}
                </span>
                <div className="bg-muted h-1.5 flex-1 overflow-hidden rounded-full">
                  <div className="bg-warning/80 h-full rounded-full" style={{ width: `${(n / misses[0]![1]) * 100}%` }} />
                </div>
                <span className="w-16 text-right font-mono tabular-nums">{count(n)}</span>
                <span className="text-muted-foreground w-12 text-right text-xs tabular-nums">{pct(n, t.rescues)}</span>
              </div>
            ))}
          </div>
          {busiest && (
            <p className="text-muted-foreground mt-4 text-xs">
              Busiest {bucketTime(busiest.ts, h.bucket_ms)} ({count(busiest.rescues)})
            </p>
          )}
        </>
      )}
    </Card>
  );
});

const Catalogue = memo(function Catalogue({ history: h }: { history: History }) {
  const c = h.catalogue;
  return (
    <Card title="Library" description="Every file you can play, right now">
      <Tiles
        flush
        tiles={[
          ["Files", count(c.files)],
          ["Total size", bytes(c.bytes)],
          ["Largest file", bytes(c.largest)],
          ["Added last 7 days", count(c.added_7d)],
        ]}
      />
    </Card>
  );
});

function since(ms: number) {
  const days = Math.floor((Date.now() - ms) / 86_400_000);
  const date = new Date(ms).toLocaleDateString([], { dateStyle: "medium" });
  if (days < 1) return `Since ${date}`;
  if (days < 60) return `Last ${days} ${days === 1 ? "day" : "days"} of activity`;
  const months = Math.floor(days / 30);
  return `Last ${months < 24 ? `${months} months` : `${Math.floor(months / 12)} years`} of activity`;
}

function readTime(ms: number) {
  const h = ms / 3_600_000;
  return h >= 48 ? `${(h / 24).toFixed(1)} d` : h >= 1 ? `${h.toFixed(1)} h` : ms >= 60_000 ? `${Math.round(ms / 60_000)} min` : `${Math.round(ms / 1000)} s`;
}

const Lifetime = memo(function Lifetime({ history: h }: { history: History }) {
  const l = h.lifetime;
  const { busiest_day: day, busiest_hour: hour } = h.records;
  return (
    <Card title="All time" description={h.records.since == null ? "No history yet" : since(h.records.since)}>
      <Tiles
        flush
        tiles={[
        ["Read", bytes(l.served), `${bytes(l.fetched)} from providers`],
        ["Articles", count(l.requests), `${count(l.rescues)} rescued`],
        ["Read sessions", count(l.reads)],
        ["Time reading", readTime(l.read_ms)],
        ["Busiest day", day ? bytes(day.bytes) : "–", day && `downloaded ${new Date(day.at).toLocaleDateString([], { dateStyle: "medium", timeZone: "UTC" })}`],
        [
          "Busiest hour",
          hour ? bytes(hour.bytes) : "–",
          hour && `downloaded ${new Date(hour.at).toLocaleString([], { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" })}`,
        ],
      ]}
      />
    </Card>
  );
});
