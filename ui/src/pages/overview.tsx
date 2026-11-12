import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ChevronRight, CircleAlert, Info } from "lucide-react";
import { type PointerEvent, type ReactNode, memo, useLayoutEffect, useRef, useState } from "react";
import { Link } from "react-router";
import { api, body, type Schemas } from "../api/client";
import { ago, bytes, duration, stamp } from "../format";
import { cn } from "@/lib/utils";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Skeleton } from "@/components/ui/skeleton";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { Empty, Failure, InfoTip, PageHeader, Panel, ReleaseName, Status, type Tone } from "../ui";

export type Window = Schemas["Window"];
export type Live = Schemas["LiveView"];
export type History = Schemas["Overview"];

const windows: Window[] = ["1h", "24h", "7d", "30d", "all"];
export const spans: Record<Window, string> = { "1h": "last hour", "24h": "last 24 h", "7d": "last 7 days", "30d": "last 30 days", all: "all time" };

export const series = [
  { key: "stream", bytes: "stream_bytes", label: "Playback", color: "var(--color-chart-playback)" },
  { key: "queue", bytes: "queue_bytes", label: "Imports", color: "var(--color-chart-imports)" },
  { key: "health", bytes: "health_bytes", label: "Health checks", color: "var(--color-primary)" },
] as const;

export const rate = (bps: number, compact = false) => `${bytes(bps, compact)}/s`;
/** With a noun, "1 trip" or "3 trips". */
export const count = (n: number, one?: string, many = `${one}s`) =>
  one ? `${n.toLocaleString()} ${n === 1 ? one : many}` : n.toLocaleString();
// Cached and local reads round down to 0 ms.
export const ms = (v: number) => (v < 1 ? "<1 ms" : `${v} ms`);
export const pct = (part: number, whole: number) => {
  if (whole <= 0) return "–";
  const p = (part / whole) * 100;
  return p > 0 && p < 0.01 ? "<0.01%" : `${p.toFixed(p >= 10 || p === 0 ? 0 : 2)}%`;
};
export const name = (path: string) => path.split("/").pop() || path;
/** The file name, or its folder's when the file name does not contain the folder's start (obfuscated). */
export const displayName = (path: string) => {
  const parts = path.split("/").filter(Boolean);
  const file = parts.at(-1) ?? path;
  // Below the root and category folders, so the parent is a release folder.
  const folder = parts.length >= 4 ? (parts.at(-2) ?? "") : "";
  return folder && !file.toLowerCase().includes(folder.slice(0, 10).toLowerCase()) ? folder : file;
};
const bucketLabel = (ms: number) =>
  ms > 86_400_000 ? "week" : ms === 86_400_000 ? "day" : ms >= 3_600_000 ? `${ms / 3_600_000} h` : ms === 60_000 ? "min" : `${ms / 60_000} min`;
export const bucketTime = (ts: number, ms: number) => {
  const time = new Date(ts).toLocaleString([], ms >= 86_400_000 ? { dateStyle: "medium" } : { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" });
  return ms > 86_400_000 ? `Week of ${time}` : time;
};

/** Metrics for one window; Overview and Stats share the cache. */
export function useHistory(span: Window) {
  return useQuery({
    queryKey: ["overview", span],
    queryFn: () => body(api.GET("/api/v1/overview", { params: { query: { window: span } } })),
    refetchInterval: 60_000,
    placeholderData: keepPreviousData,
  });
}

export function WindowPicker({ value, onChange }: { value: Window; onChange: (span: Window) => void }) {
  return (
    <ToggleGroup type="single" variant="outline" aria-label="Window" value={value} onValueChange={(v) => v && onChange(v as Window)}>
      {windows.map((w) => (
        <ToggleGroupItem key={w} value={w} className="data-[state=on]:bg-primary/15 data-[state=on]:text-primary px-3">
          {w === "all" ? "All" : w.replace(/(\d+)/, "$1 ")}
        </ToggleGroupItem>
      ))}
    </ToggleGroup>
  );
}

export function WriterError({ error }: { error?: string | null }) {
  if (!error) return null;
  return (
    <Alert variant="destructive" className="mb-6">
      <CircleAlert />
      <AlertDescription>{error} Live numbers still work; history resumes once it can be saved.</AlertDescription>
    </Alert>
  );
}

export function Overview() {
  const [span, setSpan] = useState<Window>("24h");
  // Kept fresh by the `live` server-sent event; the request only fills the first paint.
  const live = useQuery({
    queryKey: ["live"],
    queryFn: () => body(api.GET("/api/v1/live")),
    staleTime: Infinity,
  });
  const history = useHistory(span);

  return (
    <>
      <PageHeader title="Overview" />
      <Imported />
      <Leftovers />
      <WriterError error={live.data?.writer_error ?? history.data?.writer_error} />
      <Attention live={live.data} />
      <div className="space-y-6">
        {live.isPending ? (
          <Card title="Right now">
            <TilesSkeleton n={6} flush />
          </Card>
        ) : live.isError ? (
          <Failure error={live.error} />
        ) : (
          <Now live={live.data} />
        )}
        <section className="space-y-6">
          <div className="flex flex-wrap items-end justify-between gap-3">
            <div>
              <h2 className="font-medium">History</h2>
              <p className="text-muted-foreground text-xs">
                Activity, {spans[span]}. Providers, workloads and rescues are in{" "}
                <Link to="/stats" className="text-foreground relative underline underline-offset-2 after:absolute after:-inset-y-1 after:inset-x-0">
                  Stats
                </Link>
                .
              </p>
            </div>
            <WindowPicker value={span} onChange={setSpan} />
          </div>
          {history.isPending ? (
            <>
              <Card sub title="Activity">
                <Skeleton style={{ height: HEIGHT }} />
              </Card>
              <TilesSkeleton n={7} tall />
            </>
          ) : history.isError ? (
            <Failure error={history.error} />
          ) : (
            <Windowed history={history.data} span={span} />
          )}
        </section>
        {history.data && <Reads reads={history.data.reads} />}
      </div>
    </>
  );
}

/** What needs a decision now; each row links to where it is fixed. Renders nothing when all is well. */
function Attention({ live }: { live?: Live }) {
  const summary = useQuery({ queryKey: ["health", "summary"], queryFn: () => body(api.GET("/api/v1/health/summary")) }).data;
  const paused = useQuery({ queryKey: ["queue", "paused"], queryFn: () => body(api.GET("/api/v1/queue/paused")) }).data?.paused;
  const status = useQuery({ queryKey: ["status"], queryFn: () => body(api.GET("/api/v1/status")) }).data;
  const arr = useQuery({ queryKey: ["arr-status"], queryFn: () => body(api.GET("/api/v1/arr/status")), refetchInterval: 30_000 }).data;
  const actionNeeded = summary?.find((s) => s.state === "action_needed")?.files ?? 0;
  const arrIssues = arr?.instances.reduce((n, i) => n + i.queue_issues.length, 0) ?? 0;
  const down = live?.providers.filter((p) => p.open_for_ms != null) ?? [];
  const items: { to: string; tone: Tone; text: string }[] = [];
  if (actionNeeded > 0) items.push({ to: "/health", tone: "warning", text: `${count(actionNeeded, "file")} ${actionNeeded === 1 ? "needs" : "need"} action` });
  if (arrIssues > 0) {
    items.push({ to: "/queue", tone: "warning", text: `${count(arrIssues, "download")} ${arrIssues === 1 ? "needs" : "need"} you in the Arr before it can import` });
  }
  for (const p of down) {
    items.push({ to: "/settings/providers", tone: "destructive", text: `${p.name} is paused for ${Math.ceil((p.open_for_ms ?? 0) / 1000)} s after errors` });
  }
  if (paused) items.push({ to: "/queue", tone: "warning", text: "The queue is paused" });
  if (status?.mount.enabled && !status.mount.mounted) {
    items.push({ to: "/system", tone: "warning", text: `Not mounted${status.mount.reason ? `: ${status.mount.reason}` : ""}` });
  }
  if (status?.backup_warning) items.push({ to: "/settings/backups", tone: "warning", text: status.backup_warning });
  if (items.length === 0) return null;
  return (
    <section aria-labelledby="attention" className="mb-6">
      <h2 id="attention" className="sr-only">
        Needs attention
      </h2>
      <ul className="bg-card divide-y rounded-lg border">
        {items.map((item) => (
          <li key={item.text}>
            <Link to={item.to} className="hover:bg-muted/50 flex items-center gap-3 px-4 py-2.5">
              <Status tone={item.tone} className="min-w-0 flex-1 whitespace-normal">
                <span>{item.text}</span>
              </Status>
              <ChevronRight className="text-muted-foreground size-4 shrink-0" aria-hidden />
            </Link>
          </li>
        ))}
      </ul>
    </section>
  );
}

/** Once, after the upgrade from C#: what was carried over and what was left behind. */
function Imported() {
  const client = useQueryClient();
  const report = useQuery({ queryKey: ["upgrade", "report"], queryFn: () => body(api.GET("/api/v1/upgrade/report")) });
  const [open, setOpen] = useState(false);
  const seen = useMutation({
    mutationFn: () => body(api.POST("/api/v1/upgrade/report/seen")),
    onSuccess: () => client.invalidateQueries({ queryKey: ["upgrade", "report"] }),
  });
  const text = report.data?.report;
  if (!text || report.data?.seen) return null;
  return (
    <Alert className="mb-6">
      <Info />
      <AlertTitle>Imported from your C# install</AlertTitle>
      <AlertDescription>
        Your files, history, queue and settings were carried over. The report lists what was left behind and where empty
        values came from.
        <div className="mt-2 flex gap-2">
          <Button size="sm" variant="outline" onClick={() => setOpen(true)}>
            View report
          </Button>
          <Button size="sm" variant="ghost" disabled={seen.isPending} onClick={() => seen.mutate()}>
            Dismiss
          </Button>
        </div>
      </AlertDescription>
      <ImportReportDialog text={text} open={open} onOpenChange={setOpen}>
        <Button onClick={() => seen.mutate(undefined, { onSuccess: () => setOpen(false) })} disabled={seen.isPending}>
          Done
        </Button>
      </ImportReportDialog>
    </Alert>
  );
}

/** After the upgrade: the 1.x data still on disk. Measuring it is left to System. */
function Leftovers() {
  const client = useQueryClient();
  const view = useQuery({ queryKey: ["upgrade", "leftovers"], queryFn: () => body(api.GET("/api/v1/upgrade/leftovers")) }).data;
  const report = useQuery({ queryKey: ["upgrade", "report"], queryFn: () => body(api.GET("/api/v1/upgrade/report")) }).data?.report;
  const [open, setOpen] = useState(false);
  const dismiss = useMutation({
    mutationFn: () => body(api.POST("/api/v1/upgrade/leftovers/dismiss")),
    onSuccess: () => client.invalidateQueries({ queryKey: ["upgrade", "leftovers"] }),
  });
  if (!view?.imported || !view.present || view.in_use || view.dismissed) return null;
  return (
    <Alert className="mb-6">
      <Info />
      <AlertTitle>Some 1.x data is still in your config folder</AlertTitle>
      <AlertDescription>
        Once you have checked that 2.x plays your library, review it on System. System shows which paths you can delete
        and estimates their size.
        <div className="mt-2 flex flex-wrap gap-2">
          <Button asChild size="sm" variant="outline">
            <Link to="/system">View on System</Link>
          </Button>
          {report && (
            <Button size="sm" variant="outline" onClick={() => setOpen(true)}>
              View import report
            </Button>
          )}
          <Button size="sm" variant="ghost" disabled={dismiss.isPending} onClick={() => dismiss.mutate()}>
            Dismiss
          </Button>
        </div>
      </AlertDescription>
      {report && <ImportReportDialog text={report} open={open} onOpenChange={setOpen} />}
    </Alert>
  );
}

export function ImportReportDialog({
  text,
  open,
  onOpenChange,
  children,
}: {
  text: string;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  children?: ReactNode;
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-3xl">
        <DialogHeader>
          <DialogTitle>Import report</DialogTitle>
          <DialogDescription>What the upgrade from C# carried over and what it left behind.</DialogDescription>
        </DialogHeader>
        <pre className="bg-muted/50 max-h-[60vh] overflow-auto rounded-md border p-3 font-mono text-xs whitespace-pre-wrap">
          {text}
        </pre>
        {children && <DialogFooter>{children}</DialogFooter>}
      </DialogContent>
    </Dialog>
  );
}

export function Card({ title, description, action, children, sub }: { title: ReactNode; description?: ReactNode; action?: ReactNode; children: ReactNode; sub?: boolean }) {
  const Heading = sub ? "h3" : "h2";
  return (
    <section className="bg-card rounded-lg border p-5">
      <div className="mb-4 flex flex-wrap items-start gap-x-6 gap-y-3">
        <div className="min-w-0 flex-1 basis-48">
          <Heading className="font-medium">{title}</Heading>
          {description && <p className="text-muted-foreground mt-0.5 text-xs">{description}</p>}
        </div>
        {action}
      </div>
      {children}
    </section>
  );
}

// Stretch the last tile when needed so no row ends in an empty cell.
const grids: Record<number, string> = {
  4: "grid-cols-2 sm:grid-cols-4",
  5: "grid-cols-2 sm:grid-cols-3 lg:grid-cols-5 [&>:last-child]:col-span-2 lg:[&>:last-child]:col-span-1",
  6: "grid-cols-2 sm:grid-cols-3 lg:grid-cols-6",
  7: "grid-cols-2 sm:grid-cols-3 lg:grid-cols-4 [&>:last-child]:col-span-2 sm:[&>:last-child]:col-span-3 lg:[&>:last-child]:col-span-2",
};

/** Placeholder for `Tiles` with the same footprint; `tall` leaves room for sparklines. */
function TilesSkeleton({ n, tall, flush }: { n: number; tall?: boolean; flush?: boolean }) {
  return (
    <div className={cn("bg-border grid gap-px overflow-hidden", !flush && "rounded-lg border", grids[n])} aria-busy="true">
      {Array.from({ length: n }, (_, i) => (
        <div key={i} className={cn("bg-card space-y-2 p-3", tall ? "h-29" : "h-21")}>
          <Skeleton className="h-3 w-16" />
          <Skeleton className="h-6 w-20" />
        </div>
      ))}
    </div>
  );
}

/** `flush` drops the outer border for tiles inside a card, which is already raised. */
export function Tiles({ tiles, flush }: { tiles: [ReactNode, ReactNode, ReactNode?, ReactNode?][]; flush?: boolean }) {
  return (
    <dl className={cn("bg-border grid gap-px overflow-hidden", !flush && "rounded-lg border", grids[tiles.length])}>
      {tiles.map(([label, value, hint, spark], i) => (
        <div key={i} className="bg-card flex min-w-0 flex-col p-3">
          <dt className="text-muted-foreground text-xs">{label}</dt>
          <dd className="mt-0.5 font-mono text-lg font-medium tabular-nums">{value}</dd>
          {hint && <dd className="text-muted-foreground text-xs">{hint}</dd>}
          {spark && <dd className="mt-auto pt-2">{spark}</dd>}
        </div>
      ))}
    </dl>
  );
}

function Now({ live }: { live: Live }) {
  const par2 = useQuery({
    queryKey: ["par2-scan"],
    queryFn: () => body(api.GET("/api/v1/health/par2-scan")),
    refetchInterval: 5000,
  });
  const work = (state: number) => par2.data?.counts.find((group) => group.state === state)?.count ?? 0;
  let repairStatus = "Loading…";
  if (par2.isError) repairStatus = "Unavailable";
  else if (par2.data) {
    if (!par2.data.enabled) repairStatus = "Disabled";
    else if (work(1) > 0) repairStatus = `${count(work(1))} running`;
    else if (work(0) > 0) repairStatus = "Queued";
    else if (work(4) > 0) repairStatus = "Deferred";
    else repairStatus = "Idle";
  }
  const nearCap = live.article_cap > 0 && live.article_bytes >= live.article_cap * 0.9;
  const open = live.providers.reduce((n, p) => n + p.live, 0);
  // `live` and `active` are sampled separately and can briefly disagree.
  const warm = live.providers.reduce((n, p) => n + Math.max(0, p.live - p.active), 0);
  return (
    <Card
      title={
        <Status tone={live.reads.length > 0 ? "success" : "muted"}>
          <span>Right now</span>
        </Status>
      }
    >
      <Tiles
        flush
        tiles={[
          [
            "Active reads",
            live.reads.length,
            <>
              {count(open, "connection")}
              {warm > 0 && (
                <span className="block whitespace-nowrap sm:inline">
                  <span className="hidden sm:inline"> · </span>
                  {`${warm.toLocaleString()} warm `}
                  <InfoTip label="About warm connections">
                    Open connections waiting for work; a play reuses them without a new handshake.
                  </InfoTip>
                </span>
              )}
            </>,
          ],
          ["Requests / s", (live.requests_minute / 60).toFixed(1), `${count(live.requests_minute)} / min`],
          ["Read throughput", rate(live.read_bps), `${bytes(live.read_minute)} / min`],
          [
            "Article memory",
            <span className={cn(nearCap && "text-warning-text")}>{bytes(live.article_bytes)}</span>,
            live.article_cap > 0 ? `of ${bytes(live.article_cap)} cap` : undefined,
          ],
          [
            "Fetch errors",
            <span className={cn(live.failures_minute > 0 && "text-destructive")}>{count(live.failures_minute)}</span>,
            "failed requests / min",
          ],
          [
            <Link className="underline underline-offset-2" to="/health">
              PAR2
            </Link>,
            repairStatus,
            par2.isError ? (
              <span role="alert">
                Could not load repair status. <Link className="underline" to="/health">View details</Link>
              </span>
            ) : par2.data && (
              <>
                <span className="block">{count(work(0))} queued · {count(work(4))} deferred</span>
                {par2.data.jobs.filter((job) => job.state === "running").map((job) => (
                  <span className="mt-1 block wrap-anywhere" key={job.id}>
                    {job.verify_only ? "Availability" : "Repair"}: {job.reason}
                    {job.verify_only && job.total_articles > 0 && (
                      <span className="block">{count(job.checked_articles)} of {count(job.total_articles)} articles checked</span>
                    )}
                    {!job.verify_only && job.completed_requests > 0 && (
                      <span className="block">{count(job.completed_requests)} provider reads · {count(job.missing_requests)} missing</span>
                    )}
                  </span>
                ))}
              </>
            ),
          ],
        ]}
      />
      {live.reads.length === 0 ? (
        <p className="text-muted-foreground mt-4 border-t pt-4">Nothing is being read. Files opened through the mount or WebDAV show up here.</p>
      ) : (
        <Table className="mt-4">
          <TableHeader>
            <TableRow>
              <TableHead>File</TableHead>
              <TableHead className="hidden w-40 md:table-cell">Position</TableHead>
              <TableHead className="text-right">Rate</TableHead>
              <TableHead className="hidden text-right sm:table-cell">First byte</TableHead>
              <TableHead className="hidden text-right sm:table-cell">Stalls</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {live.reads.map((r) => (
              <TableRow key={r.id}>
                <TableCell className="w-full max-w-0">
                  <div className="truncate font-medium" title={r.path}>
                    <ReleaseName name={displayName(r.path)} />
                  </div>
                  <div className="text-muted-foreground text-xs whitespace-normal wrap-anywhere">
                    {r.delivery === "fuse" ? "FUSE" : "WebDAV"} · <Client client={r.client} /> · opened {ago(r.opened_at)}
                  </div>
                  <Sources sources={r.sources} />
                </TableCell>
                <TableCell className="hidden md:table-cell">
                  <div className="flex items-center gap-2">
                    <div className="bg-muted h-1.5 w-28 flex-none overflow-hidden rounded-full">
                      <div className="bg-primary h-full" style={{ width: `${r.size > 0 ? (r.position / r.size) * 100 : 0}%` }} />
                    </div>
                    <span className="text-muted-foreground w-9 text-right text-xs tabular-nums">
                      {r.size > 0 ? Math.floor((r.position / r.size) * 100) : 0}%
                    </span>
                  </div>
                </TableCell>
                <TableCell className="text-right tabular-nums">{rate(r.bps)}</TableCell>
                <TableCell className="hidden text-right tabular-nums sm:table-cell">{r.first_byte_ms == null ? "–" : ms(r.first_byte_ms)}</TableCell>
                <TableCell className={cn("hidden text-right tabular-nums sm:table-cell", r.stalls > 0 && "text-warning-text")}>
                  {r.stalls > 0 ? `${r.stalls} · ${(r.stall_ms / 1000).toFixed(1)} s` : "0"}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
    </Card>
  );
}

/** Which providers fetched a read's articles: share of articles and their current rate. */
function Sources({ sources }: { sources: Schemas["LiveSource"][] }) {
  const total = sources.reduce((n, s) => n + s.articles, 0);
  if (total === 0) return null;
  return (
    <div className="mt-0.5 flex flex-wrap gap-x-3 text-xs">
      {sources.map((s, i) => (
        <span key={i} className="whitespace-nowrap" title={`${count(s.articles, "article")}, ${bytes(s.bytes)}`}>
          {s.name || "Unknown provider"}{" "}
          <span className="text-muted-foreground tabular-nums">
            {s.articles < total && s.articles / total > 0.99 ? ">99%" : pct(s.articles, total)}
            {s.bps > 0 && ` · ${rate(s.bps)}`}
          </span>
        </span>
      ))}
    </div>
  );
}

// Memoised so the `live` tick every 2 s does not redraw the history.
const Windowed = memo(function Windowed({ history: h, span }: { history: History; span: Window }) {
  const t = h.totals;
  const p = h.points;
  const checks = t.healthy + t.unhealthy + t.inconclusive;
  const imports = t.imports + t.import_failures;
  // Health checks and backup providers miss by design; playback misses are the ones a viewer feels.
  const play = h.workloads.find((w) => w.workload === "stream");
  const trend = (values: number[], color: string, opts: { max?: number; bars?: boolean } = {}) => (
    <Spark values={values} color={color} {...opts} className="h-6 w-full" />
  );
  const ratio = (part: number, whole: number) => (whole > 0 ? part / whole : 0);
  return (
    <>
      <Activity history={h} span={span} />
      <Tiles
        tiles={[
          [
            "Reads",
            count(t.reads),
            t.first_byte_ms_avg == null ? "none finished" : `first byte ${ms(t.first_byte_ms_avg)} avg`,
            trend(p.map((x) => x.reads), "var(--color-primary)", { bars: true }),
          ],
          [
            "Stalls",
            <span className={cn(t.stalls > 0 && "text-warning-text")}>{count(t.stalls)}</span>,
            t.read_ms > 0 ? `${(t.stall_ms / 1000).toFixed(1)} s · ${pct(t.stall_ms, t.read_ms)} of read time` : count(t.seeks, "seek"),
            trend(p.map((x) => x.stalls), "var(--color-warning)", { bars: true }),
          ],
          [
            <span className="inline-flex items-center gap-1">
              Provider misses
              <InfoTip label="About provider misses">
                Playback requests a provider answered without the article. Another provider may still fetch it, so the viewer may
                not notice a miss.
              </InfoTip>
            </span>,
            pct(play?.missing ?? 0, play?.requests ?? 0),
            `${count(play?.missing ?? 0)} of ${count(play?.requests ?? 0)} playback requests`,
          ],
          [
            "Imports",
            count(t.imports),
            [
              imports > 0 && `${duration(Math.round(t.import_ms / imports / 1000))} avg`,
              t.import_failures > 0 && `${count(t.import_failures)} failed`,
              t.import_retries > 0 && `${count(t.import_retries)} retried`,
              t.import_failures === 0 && t.import_retries === 0 && `${bytes(t.import_bytes)} imported`,
            ]
              .filter(Boolean)
              .join(" · "),
            trend(p.map((x) => x.imports + x.import_failures), "var(--color-chart-imports)", { bars: true }),
          ],
          [
            "Health checks",
            count(checks),
            checks > 0
              ? [`${count(t.unhealthy)} unhealthy`, t.inconclusive > 0 && `${count(t.inconclusive)} inconclusive`, `${count(t.repaired)} repaired`]
                  .filter(Boolean)
                  .join(" · ")
              : "none run",
            trend(p.map((x) => x.checks), "var(--color-primary)", { bars: true }),
          ],
          [
            "Cache",
            pct(t.cache_hits, t.cache_hits + t.cache_misses),
            "segments served from cache",
            trend(p.map((x) => ratio(x.cache_hits, x.cache_hits + x.cache_misses)), "var(--color-primary)", { max: 1 }),
          ],
          [
            <span className="inline-flex items-center gap-1">
              <Link className="underline underline-offset-2" to="/health">
                PAR2 repairs
              </Link>
              <InfoTip label="About PAR2 repair history">
                Latest job outcomes updated in this window. Retries replace earlier results. Availability checks are excluded.
              </InfoTip>
            </span>,
            `${count(h.par2.repaired)} repaired`,
            `${count(h.par2.verified)} verified · ${count(h.par2.unavailable)} could not repair · ${count(h.par2.cancelled)} cancelled`,
          ],
        ]}
      />
    </>
  );
});

/** Room above the top gridline for its value, the bars, the gap and the error lane, px. */
const LABEL = 16;
const BARS = 148;
const GAP = 6;
const LANE = 12;
const HEIGHT = LABEL + BARS + GAP + LANE;

/** The smallest round number at or above `v`: 1, 1.2, 1.5, 2, 2.5, 3, 4, 5, 6 or 8 times a power of ten. */
const nice = (v: number) => {
  const p = 10 ** Math.floor(Math.log10(v));
  return p * ([1, 1.2, 1.5, 2, 2.5, 3, 4, 5, 6, 8].find((m) => m * p >= v) ?? 10);
};

type Point = History["points"][number];

const Activity = memo(function Activity({ history: h, span }: { history: History; span: Window }) {
  const [hover, setHover] = useState<number | null>(null);
  const [hidden, setHidden] = useState<ReadonlySet<string>>(new Set());
  const points = h.points;
  const t = h.totals;
  const unit = bucketLabel(h.bucket_ms);
  const n = Math.max(points.length, 1);
  const last = points.length - 1;
  // The last bucket is still filling, so its rates cover only the time it has run.
  const secs = (i: number) => (i === last ? Math.min(h.bucket_ms, Math.max(1000, h.at - points[i]!.ts)) : h.bucket_ms) / 1000;
  const shown = series.filter((s) => !hidden.has(s.key));
  const peak = Math.max(0, ...points.map((p, i) => shown.reduce((sum, s) => sum + p[s.bytes], 0) / secs(i)));
  const top = peak > 0 ? nice(peak) : 0;
  const worst = Math.max(0, ...points.map((p) => p.errors));
  const sum = (key: "errors" | (typeof series)[number]["bytes"]) => points.reduce((s, p) => s + p[key], 0);
  const errors = sum("errors");
  const peakFetch = Math.max(t.peak_fetch_bps, ...points.map((p) => p.peak_fetch_bps));
  const peakRead = Math.max(0, ...points.map((p) => p.peak_bps));
  const empty = points.every((p) => p.fetched === 0 && p.errors === 0);
  const at = hover == null ? undefined : points[hover];
  const fade = (i: number) => (hover != null && i !== hover ? 0.35 : 1) * (i === last ? 0.5 : 1);
  const toggle = (key: string) =>
    setHidden((prev) => {
      const next = new Set(prev);
      if (!next.delete(key)) next.add(key);
      return next;
    });
  const pick = (e: PointerEvent<HTMLElement>) => {
    const r = e.currentTarget.getBoundingClientRect();
    setHover(Math.min(n - 1, Math.max(0, Math.floor(((e.clientX - r.left) / r.width) * n))));
  };
  const plot = useRef<HTMLDivElement>(null);
  const tip = useRef<HTMLDivElement>(null);
  // Beside the hovered bar on its roomier side, pulled in as far as the plot's edges need.
  useLayoutEffect(() => {
    const box = plot.current;
    const el = tip.current;
    if (hover == null || !box || !el) return;
    const slot = box.clientWidth / n;
    const beside = hover < n / 2 ? (hover + 1) * slot + 8 : hover * slot - 8 - el.offsetWidth;
    el.style.left = `${Math.max(0, Math.min(beside, box.clientWidth - el.offsetWidth))}px`;
  });
  /** One bucket's bars from the bottom up, 1 px apart. */
  const stack = (p: Point, i: number) => {
    let base = LABEL + BARS;
    return shown.map((s) => {
      const gap = base < LABEL + BARS ? 1 : 0;
      const height = top > 0 ? (p[s.bytes] / secs(i) / top) * BARS - gap : 0;
      // Thinner than half a pixel would only smudge.
      if (height < 0.5) return null;
      base -= height + gap;
      return <rect key={s.key} x={i + 0.15} y={base} width={0.7} height={height} fill={s.color} />;
    });
  };

  return (
    <Card
      sub
      title="Activity"
      description={`Average download speed per ${unit}, ${spans[span]}`}
      action={
        <dl className="grid w-full grid-cols-2 gap-x-6 gap-y-2 sm:flex sm:w-auto sm:gap-x-8 sm:text-right">
          {(
            [
              ["Downloaded", t.fetched, peakFetch],
              ["Served", t.served, peakRead],
            ] as const
          ).map(([label, total, fastest]) => (
            <div key={label}>
              <dt className="text-muted-foreground text-xs">{label}</dt>
              <dd className="font-mono text-lg font-medium tabular-nums">{bytes(total, true)}</dd>
              {fastest > 0 && <dd className="text-muted-foreground text-xs tabular-nums">peak {rate(fastest, true)}</dd>}
            </div>
          ))}
        </dl>
      }
    >
      <div className="-mx-2 mb-2 flex flex-wrap items-center text-xs">
        {series.map((s) => {
          const off = hidden.has(s.key);
          return (
            <button
              key={s.key}
              type="button"
              aria-pressed={!off}
              onClick={() => toggle(s.key)}
              className={cn("hover:bg-muted inline-flex items-center gap-1.5 rounded-md px-2 py-1.5", off && "text-muted-foreground line-through")}
            >
              <span className={cn("size-2 rounded-full", off && "opacity-40")} style={{ background: s.color }} aria-hidden />
              {s.label} · <span className="tabular-nums">{bytes(sum(s.bytes), true)}</span>
            </button>
          );
        })}
        <span className="inline-flex items-center gap-1.5 px-2 py-1.5">
          <span className="bg-destructive size-2 rounded-full" aria-hidden />
          Errors · <span className={cn("tabular-nums", errors > 0 && "text-destructive")}>{count(errors)}</span>
        </span>
      </div>
      <div
        ref={plot}
        className="focus-visible:ring-ring/50 relative min-w-0 touch-pan-y rounded-sm outline-none focus-visible:ring-[3px]"
        tabIndex={0}
        role="group"
        aria-label="Download speed over time. Arrow keys step through the buckets."
        onPointerDown={pick}
        onPointerMove={pick}
        onPointerLeave={(e) => e.pointerType === "mouse" && setHover(null)}
        onFocus={() => setHover((i) => i ?? n - 1)}
        onBlur={() => setHover(null)}
        onKeyDown={(e) => {
          if (e.key !== "ArrowLeft" && e.key !== "ArrowRight") return;
          e.preventDefault();
          setHover((i) => Math.min(n - 1, Math.max(0, (i ?? n - 1) + (e.key === "ArrowLeft" ? -1 : 1))));
        }}
      >
        <svg viewBox={`0 0 ${n} ${HEIGHT}`} preserveAspectRatio="none" className="block w-full" style={{ height: HEIGHT }} aria-hidden>
          {[LABEL, LABEL + BARS / 2, LABEL + BARS].map((y) => (
            <line key={y} x1={0} x2={n} y1={y} y2={y} stroke="var(--color-border)" vectorEffect="non-scaling-stroke" />
          ))}
          {points.map((p, i) => {
            const lane = p.errors > 0 ? Math.max(2, (p.errors / worst) * LANE) : 0;
            return (
              <g key={p.ts} opacity={fade(i)}>
                {stack(p, i)}
                {lane > 0 && <rect x={i + 0.15} y={HEIGHT - lane} width={0.7} height={lane} fill="var(--color-destructive)" />}
              </g>
            );
          })}
        </svg>
        {top > 0 && <span className="text-muted-foreground pointer-events-none absolute top-0 left-0 text-xs leading-4 tabular-nums">{rate(top, true)}</span>}
        {empty && (
          <p className="pointer-events-none absolute inset-x-0 flex items-center justify-center" style={{ top: LABEL, height: BARS }}>
            <span className="bg-card text-muted-foreground px-2">No data for this window</span>
          </p>
        )}
        <span className="sr-only" aria-live="polite">
          {at &&
            hover != null &&
            `${bucketTime(at.ts, h.bucket_ms)}${hover === last ? " so far" : ""}: ${shown.map((s) => `${s.label} ${rate(at[s.bytes] / secs(hover))}`).join(", ")}, ${count(at.errors, "error")}`}
        </span>
        {at && hover != null && (
          <div
            ref={tip}
            className="bg-popover text-popover-foreground pointer-events-none absolute top-2 z-10 w-max max-w-full rounded-md border px-3 py-2 font-mono text-xs shadow-md"
          >
            <div className="mb-1 font-medium">
              {bucketTime(at.ts, h.bucket_ms)}
              {hover === last && " · so far"}
            </div>
            {shown.map((s) => (
              <div key={s.key} className="flex items-center gap-2">
                <span className="size-2 rounded-full" style={{ background: s.color }} aria-hidden />
                {rate(at[s.bytes] / secs(hover))} {s.label.toLowerCase()}
              </div>
            ))}
            {at.errors > 0 && <div className="text-destructive">{count(at.errors, "error")}</div>}
            <div className="text-muted-foreground mt-1">
              {bytes(at.fetched)} downloaded{at.peak_fetch_bps > 0 && `, peak ${rate(at.peak_fetch_bps)}`}
            </div>
            <div className="text-muted-foreground">
              {bytes(at.served)} served{at.peak_bps > 0 && `, peak ${rate(at.peak_bps)}`}
            </div>
          </div>
        )}
        {points.length > 0 && (
          <div className="text-muted-foreground mt-2 flex justify-between text-xs tabular-nums">
            <span>{bucketTime(points[0]!.ts, h.bucket_ms)}</span>
            <span className="hidden sm:inline">{bucketTime(points[Math.floor(points.length / 2)]!.ts, h.bucket_ms)}</span>
            <span>now</span>
          </div>
        )}
      </div>
    </Card>
  );
});

/** A tiny area chart; `max` shares one scale across rows. */
export function Spark({ values, color, max, bars, className = "h-7 w-28" }: { values: number[]; color: string; max?: number; bars?: boolean; className?: string }) {
  const n = Math.max(values.length, 1);
  const top = Math.max(1e-9, max ?? Math.max(...values, 0));
  const h = 28;
  const y = (v: number) => h - 1 - (v / top) * (h - 3);
  const pts = values.map((v, i) => `${i + 0.5},${y(v)}`).join(" ");
  return (
    <svg viewBox={`0 0 ${n} ${h}`} preserveAspectRatio="none" className={cn("block", className)} aria-hidden>
      <line x1={0} x2={n} y1={h - 1} y2={h - 1} stroke="var(--color-border)" vectorEffect="non-scaling-stroke" />
      {!(top > 1e-9 && values.some((v) => v > 0)) ? null : bars ? (
        values.map((v, i) => (v > 0 ? <rect key={i} x={i + 0.1} y={y(v)} width={0.8} height={h - 1 - y(v)} fill={color} /> : null))
      ) : (
        <>
          <polygon points={`0.5,${h - 1} ${pts} ${n - 0.5},${h - 1}`} fill={color} opacity={0.15} />
          <polyline points={pts} fill="none" stroke={color} strokeWidth={1.25} strokeLinejoin="round" vectorEffect="non-scaling-stroke" />
        </>
      )}
    </svg>
  );
}

type Read = Schemas["RecentRead"];

/** Players reopen the file on every seek; reopens by one program within this gap are one playback. */
const SAME_PLAYBACK_MS = 120_000;
const program = (r: Read) => `${r.delivery === "fuse" ? "FUSE" : "WebDAV"} · ${r.client.replace(/ \(pid [^)]*\)/, "") || "unknown client"}`;

/** A FUSE caller's process name and, apart, the ids behind it; only the name when the process is hidden. */
const caller = (client: string) => {
  const m = /^(.+?) \((pid [^)]*)\) · (uid .*)$/.exec(client);
  return m ? { name: m[1]!, ids: `${m[2]} · ${m[3]}` } : { name: client || "unknown client" };
};

function Client({ client }: { client: string }) {
  const { name, ids } = caller(client);
  if (!ids) return name;
  return (
    <>
      {name} <InfoTip label="Process details">{ids}</InfoTip>
    </>
  );
}

/** `reads` newest first; each playback keeps that order. */
function playbacks(reads: Read[]) {
  const groups: { latest: Read; oldest: Read; reads: Read[] }[] = [];
  for (const r of reads) {
    const group = groups.find((g) => g.oldest.path === r.path && program(g.oldest) === program(r) && g.oldest.opened_at - r.closed_at <= SAME_PLAYBACK_MS);
    if (group) {
      group.reads.push(r);
      group.oldest = r;
    } else groups.push({ latest: r, oldest: r, reads: [r] });
  }
  return groups;
}

const sum = (reads: Read[], key: "served" | "stalls" | "stall_ms" | "seeks") => reads.reduce((n, r) => n + r[key], 0);
const stallText = (stalls: number, ms: number) => (stalls > 0 ? `${stalls} · ${(ms / 1000).toFixed(1)} s` : "0");
const clock = (ms: number) => new Date(ms).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });

const READ_ROWS = 10;

const Reads = memo(function Reads({ reads }: { reads: Read[] }) {
  const [open, setOpen] = useState<Set<string>>(new Set());
  const [limit, setLimit] = useState(READ_ROWS);
  const all = playbacks(reads);
  const toggle = (key: string) =>
    setOpen((prev) => {
      const next = new Set(prev);
      if (!next.delete(key)) next.add(key);
      return next;
    });
  return (
    <section>
      <h2 className="mb-3 font-medium">Recent reads</h2>
      {reads.length === 0 ? (
        <Empty title="No finished reads yet" />
      ) : (
        <Panel>
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>File</TableHead>
                <TableHead className="hidden sm:table-cell">Closed</TableHead>
                <TableHead className="text-right">Read</TableHead>
                <TableHead className="hidden text-right sm:table-cell">First byte</TableHead>
                <TableHead className="hidden text-right sm:table-cell">
                  <span className="inline-flex items-center gap-1">
                    Stalls
                    <InfoTip label="About stalls">
                      A stall is a read that waited 1 s or more for data after the first byte arrived. The wait for the first byte
                      after a seek is not a stall. Shown as the number of stalls and their total wait. 0 is ideal. Several stalls
                      likely means the player paused to buffer. Reads from older versions may also count shorter waits
                      and waits after a seek.
                    </InfoTip>
                  </span>
                </TableHead>
                <TableHead className="hidden text-right md:table-cell">Seeks</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {all.slice(0, limit).flatMap(({ latest, oldest, reads: g }) => {
                // Keyed by the oldest read so an open playback stays open as new reads join it.
                const key = `${oldest.opened_at}-${oldest.path}-${oldest.closed_at}`;
                if (g.length === 1) return [<ReadRow key={key} read={latest} />];
                const expanded = open.has(key);
                const waits = g.flatMap((r) => (r.first_byte_ms == null ? [] : [r.first_byte_ms])).sort((a, b) => a - b);
                const stalls = sum(g, "stalls");
                return [
                  <TableRow key={key}>
                    <TableCell className="w-full max-w-0">
                      <button
                        type="button"
                        className="flex w-full min-w-0 items-start gap-1.5 text-left"
                        aria-expanded={expanded}
                        onClick={() => toggle(key)}
                      >
                        <ChevronRight className={cn("text-muted-foreground mt-0.5 size-4 shrink-0 transition-transform", expanded && "rotate-90")} />
                        <span className="min-w-0">
                          <span className="block font-medium whitespace-normal wrap-anywhere" title={latest.path}>
                            <ReleaseName name={displayName(latest.path)} />
                          </span>
                          <span className="text-muted-foreground block text-xs whitespace-normal wrap-anywhere">
                            {latest.delivery === "fuse" ? "FUSE" : "WebDAV"} · {caller(latest.client).name} · {g.length} opens
                            <span className="sm:hidden"> · {ago(latest.closed_at)}</span>
                            {stalls > 0 && <span className="text-warning-text sm:hidden"> · {count(stalls, "stall")}</span>}
                          </span>
                        </span>
                      </button>
                    </TableCell>
                    <TableCell className="text-muted-foreground hidden sm:table-cell" title={stamp(latest.closed_at)}>
                      {ago(latest.closed_at)}
                    </TableCell>
                    <TableCell className="text-right tabular-nums">{bytes(sum(g, "served"))}</TableCell>
                    <TableCell
                      className="hidden text-right tabular-nums sm:table-cell"
                      title={waits.length > 1 ? `Median of ${waits.length} opens; ${waits[0]}–${waits[waits.length - 1]} ms` : undefined}
                    >
                      {waits.length === 0 ? "–" : ms(waits[Math.floor(waits.length / 2)]!)}
                    </TableCell>
                    <TableCell className={cn("hidden text-right tabular-nums sm:table-cell", stalls > 0 && "text-warning-text")}>{stallText(stalls, sum(g, "stall_ms"))}</TableCell>
                    <TableCell className="hidden text-right tabular-nums md:table-cell">{sum(g, "seeks")}</TableCell>
                  </TableRow>,
                  ...(expanded ? g.map((r) => <ReadRow key={`${key}-${r.opened_at}-${r.closed_at}`} read={r} nested />) : []),
                ];
              })}
            </TableBody>
          </Table>
        </Panel>
      )}
      {all.length > READ_ROWS && (
        <div className="text-muted-foreground mt-3 flex items-center justify-end gap-2 text-xs">
          <span className="tabular-nums">
            {Math.min(limit, all.length)} of {all.length}
          </span>
          {limit > READ_ROWS && (
            <Button variant="ghost" size="sm" onClick={() => setLimit(READ_ROWS)}>
              Show fewer
            </Button>
          )}
          {limit < all.length && (
            <Button variant="outline" size="sm" onClick={() => setLimit((n) => n + READ_ROWS)}>
              Show more
            </Button>
          )}
        </div>
      )}
    </section>
  );
});

function ReadRow({ read: r, nested }: { read: Read; nested?: boolean }) {
  const pid = /\(pid (\d+)/.exec(r.client)?.[1];
  return (
    <TableRow className={cn(nested && "bg-muted/30")}>
      <TableCell className={cn("w-full max-w-0", nested && "pl-9")}>
        {nested ? (
          <div className="text-muted-foreground text-xs">
            Opened {clock(r.opened_at)}
            <span className="sm:hidden"> · closed {clock(r.closed_at)}</span>
            {pid && ` · pid ${pid}`}
            {r.end_reason === "eof" && " · read to the end"}
          </div>
        ) : (
          <div className="flex min-w-0 items-start gap-1.5">
            {/* Lines up with the chevron on rows that expand. */}
            <span className="size-4 shrink-0" aria-hidden />
            <div className="min-w-0">
              <div className="font-medium whitespace-normal wrap-anywhere" title={r.path}>
                <ReleaseName name={displayName(r.path)} />
              </div>
              <div className="text-muted-foreground text-xs whitespace-normal wrap-anywhere">
                {r.delivery === "fuse" ? "FUSE" : "WebDAV"} · <Client client={r.client} />
                {r.end_reason === "eof" && " · read to the end"}
                <span className="sm:hidden"> · {ago(r.closed_at)}</span>
                {r.stalls > 0 && <span className="text-warning-text sm:hidden"> · {count(r.stalls, "stall")}</span>}
              </div>
            </div>
          </div>
        )}
      </TableCell>
      <TableCell className="text-muted-foreground hidden sm:table-cell" title={stamp(r.closed_at)}>
        {nested ? clock(r.closed_at) : ago(r.closed_at)}
      </TableCell>
      <TableCell className="text-right tabular-nums">{bytes(r.served)}</TableCell>
      <TableCell className="hidden text-right tabular-nums sm:table-cell">{r.first_byte_ms == null ? "–" : ms(r.first_byte_ms)}</TableCell>
      <TableCell className={cn("hidden text-right tabular-nums sm:table-cell", r.stalls > 0 && "text-warning-text")}>{stallText(r.stalls, r.stall_ms)}</TableCell>
      <TableCell className="hidden text-right tabular-nums md:table-cell">{r.seeks}</TableCell>
    </TableRow>
  );
}
