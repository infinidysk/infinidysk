import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Archive, CircleArrowUp, Cpu, Database, Download, HardDrive, Info, Loader2, MemoryStick, Search, Tag, TriangleAlert, type LucideIcon } from "lucide-react";
import { Fragment, useDeferredValue, useEffect, useState, type ReactNode } from "react";
import { Link, useSearchParams } from "react-router";
import { api, body, HttpError, type Schemas } from "../api/client";
import { ago, bytes, stamp } from "../format";
import { jobCopy } from "../maintenance";
import { cn } from "@/lib/utils";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Textarea } from "@/components/ui/textarea";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { Empty, Failure, Field, InfoTip, Loading, PageHeader, Panel, Status, Toggle } from "../ui";
import { count, ImportReportDialog } from "./overview";
import { Tracing } from "./traces";

type Level = Schemas["Level"];

const levels: { level: Level; label: string }[] = [
  { level: "error", label: "Errors" },
  { level: "warn", label: "Warnings" },
  { level: "info", label: "Info" },
  { level: "debug", label: "Debug" },
];

const tone: Record<Level, string> = {
  error: "text-destructive",
  warn: "text-warning-text",
  info: "",
  debug: "text-muted-foreground",
  trace: "text-muted-foreground",
};

const LOG_ROWS = 50;

export function System() {
  const status = useQuery({
    queryKey: ["status"],
    queryFn: () => body(api.GET("/api/v1/status")),
    refetchInterval: 10_000,
  });
  // Kept fresh by the `live` server-sent event.
  const live = useQuery({
    queryKey: ["live"],
    queryFn: () => body(api.GET("/api/v1/live")),
    staleTime: Infinity,
  });

  return (
    <>
      <PageHeader title="System" description="Version, mount and disk cache status, CPU and memory use, read traces and recent logs">
        <SupportPack />
      </PageHeader>
      {/* A lone last card spans the row instead of leaving a hole. */}
      <div className="mb-6 grid gap-4 lg:grid-cols-2 lg:[&>:last-child:nth-child(odd)]:col-span-2">
        {status.isPending ? (
          <Loading />
        ) : status.isError ? (
          <Failure error={status.error} />
        ) : (
          <>
            <Version status={status.data} />
            <Card icon={HardDrive}>
              <div className="flex flex-wrap items-center gap-x-3">
                <Status tone={!status.data.mount.enabled ? "muted" : status.data.mount.mounted ? "success" : "warning"}>
                  <span className="font-medium">
                    {!status.data.mount.enabled ? "Built-in FUSE mount off" : status.data.mount.mounted ? "Mounted" : "Not mounted"}
                  </span>
                </Status>
                <span className="text-muted-foreground truncate font-mono text-xs" title={status.data.mount.dir}>
                  {status.data.mount.dir}
                </span>
              </div>
              {status.data.mount.reason && <p className="text-muted-foreground mt-0.5">{status.data.mount.reason}</p>}
            </Card>
          </>
        )}
        <Resources live={live.data} />
        {status.data?.disk_cache && <DiskCache cache={status.data.disk_cache} />}
        <Leftovers />
      </div>
      <Tracing />
      <Logs />
    </>
  );
}

function Version({ status }: { status: Schemas["Status"] }) {
  const report = useQuery({ queryKey: ["upgrade", "report"], queryFn: () => body(api.GET("/api/v1/upgrade/report")) }).data?.report;
  const [open, setOpen] = useState(false);
  const [notes, setNotes] = useState(false);
  return (
    <Card icon={Tag}>
      <div className="flex flex-wrap items-baseline gap-x-3">
        <span className="font-medium">infinidysk {status.version}</span>
        {status.commit && <span className="text-muted-foreground font-mono text-xs">{status.commit.slice(0, 7)}</span>}
      </div>
      <div className="mt-0.5 flex flex-wrap items-center gap-x-4">
        {status.update && (
          <a
            href={status.update.url}
            target="_blank"
            rel="noreferrer"
            className="text-warning-text flex items-center gap-1.5 underline-offset-4 hover:underline"
          >
            <CircleArrowUp className="size-4" />
            {status.update.version} available
          </a>
        )}
        <Button variant="link" size="sm" className="h-auto p-0" onClick={() => setNotes(true)}>
          View release notes
        </Button>
        <ReleaseNotesDialog installed={status.version} open={notes} onOpenChange={setNotes} />
        {report && (
          <>
            <Button variant="link" size="sm" className="h-auto p-0" onClick={() => setOpen(true)}>
              View import report
            </Button>
            <ImportReportDialog text={report} open={open} onOpenChange={setOpen} />
          </>
        )}
      </div>
    </Card>
  );
}

const released = (date: string) => new Date(date).toLocaleDateString([], { dateStyle: "medium", timeZone: "UTC" });

function ReleaseNotesDialog({ installed, open, onOpenChange }: { installed: string; open: boolean; onOpenChange: (open: boolean) => void }) {
  // The notes are built into the server, so they only change with a new version.
  const notes = useQuery({
    queryKey: ["release-notes"],
    queryFn: () => body(api.GET("/api/v1/release-notes")),
    enabled: open,
    staleTime: Infinity,
  });
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle>Release notes</DialogTitle>
          <DialogDescription>What changed in each release, newest first.</DialogDescription>
        </DialogHeader>
        {notes.isPending ? (
          <Loading />
        ) : notes.isError ? (
          <Failure error={notes.error} />
        ) : (
          <div className="space-y-6">
            {notes.data.map((release) => (
              <section key={release.version}>
                <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
                  <h3 className="font-mono font-medium">
                    {release.url ? (
                      <a href={release.url} target="_blank" rel="noreferrer" className="underline-offset-4 hover:underline">
                        {release.version}
                      </a>
                    ) : (
                      release.version
                    )}
                  </h3>
                  {release.version === installed && <Badge variant="outline">Installed</Badge>}
                  {release.date && <span className="text-muted-foreground text-xs">{released(release.date)}</span>}
                </div>
                {release.sections.map((section) => (
                  <div key={section.title} className="mt-3">
                    <h4 className="text-muted-foreground flex items-center gap-1.5 text-xs font-medium tracking-wide uppercase">
                      {section.breaking && <TriangleAlert className="text-warning-text size-3.5" aria-hidden />}
                      {section.title}
                    </h4>
                    <ul className="marker:text-muted-foreground mt-1.5 list-disc space-y-1 pl-5">
                      {section.changes.map((change, i) => (
                        <li key={i} className="text-pretty">
                          {change.scope && <span className="text-muted-foreground font-mono text-xs">{change.scope}: </span>}
                          {change.text}
                          {change.links.map((link) => (
                            <Fragment key={link.url}>
                              {" "}
                              <a
                                href={link.url}
                                target="_blank"
                                rel="noreferrer"
                                className="text-muted-foreground hover:text-foreground font-mono text-xs underline-offset-4 hover:underline"
                              >
                                {link.label}
                              </a>
                            </Fragment>
                          ))}
                        </li>
                      ))}
                    </ul>
                  </div>
                ))}
              </section>
            ))}
          </div>
        )}
      </DialogContent>
    </Dialog>
  );
}

function SupportPack() {
  const [open, setOpen] = useState(false);
  const [note, setNote] = useState("");
  const [traces, setTraces] = useState(false);
  const kept = useQuery({ queryKey: ["traces"], queryFn: () => body(api.GET("/api/v1/traces")), enabled: open });
  const count = kept.data?.length ?? 0;
  const pack = useMutation({
    mutationFn: async () => {
      const { data, error, response } = await api.POST("/api/v1/support-pack", {
        body: { note: note.trim() || null, include_traces: traces && count > 0 },
        parseAs: "blob",
      });
      if (!response.ok || !data) {
        throw new HttpError(response.status, (error as { error?: string } | undefined)?.error ?? response.statusText);
      }
      const name = /filename="([^"]+)"/.exec(response.headers.get("Content-Disposition") ?? "")?.[1];
      const a = document.createElement("a");
      a.href = URL.createObjectURL(data);
      a.download = name ?? "infinidysk-support.zip";
      a.click();
      URL.revokeObjectURL(a.href);
    },
    onSuccess: () => setOpen(false),
  });
  return (
    <Dialog
      open={open}
      onOpenChange={(next) => {
        setOpen(next);
        if (!next) pack.reset();
      }}
    >
      <Button variant="outline" size="sm" onClick={() => setOpen(true)}>
        <Download />
        Download support pack
      </Button>
      <DialogContent>
        <form
          className="grid gap-4"
          onSubmit={(e) => {
            e.preventDefault();
            pack.mutate();
          }}
        >
          <DialogHeader>
            <DialogTitle>Download support pack</DialogTitle>
            <DialogDescription>
              A ZIP of recent logs, settings, provider and queue status to attach to a bug report. It is built when you download
              it and not kept on the server.
            </DialogDescription>
          </DialogHeader>
          <Field id="support-note" label="Note" hint="Optional. What went wrong and roughly when; saved as NOTE.txt in the pack.">
            <Textarea id="support-note" rows={4} maxLength={10000} value={note} onChange={(e) => setNote(e.target.value)} />
          </Field>
          <Toggle
            id="support-traces"
            label="Include read traces"
            hint={
              count > 0
                ? `${count} ${count === 1 ? "trace is" : "traces are"} kept. They show how playback read a file, segment by segment.`
                : "No read traces are kept. Start one under Read traces on this page, reproduce the problem, then come back."
            }
            checked={traces && count > 0}
            disabled={count === 0}
            onChange={setTraces}
          />
          <p className="text-muted-foreground flex gap-2 text-xs">
            <Info className="mt-px size-3.5 shrink-0" />
            Passwords, API keys, provider usernames and public IP addresses are removed. File and release names, paths and host
            names stay, so share the pack only with people you trust.
          </p>
          {pack.isError && <Failure error={pack.error} />}
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => setOpen(false)}>
              Cancel
            </Button>
            <Button disabled={pack.isPending}>
              {pack.isPending ? <Loader2 className="animate-spin" /> : <Download />}
              Download support pack
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

function Card({ icon: Icon, children }: { icon: LucideIcon; children: ReactNode }) {
  return (
    <Panel className="flex items-center gap-4 p-4">
      <div className="bg-muted hidden size-9 shrink-0 place-items-center rounded-md sm:grid">
        <Icon className="text-muted-foreground size-4" />
      </div>
      <div className="min-w-0 flex-1">{children}</div>
    </Panel>
  );
}

const notCounted: Record<Schemas["LeftoverReason"], string> = {
  live_data: "2.x uses this path; keep it",
  symlink: "a symlink; not followed or counted",
  unreadable: "could not be read; not counted",
  changed: "appeared since the last measurement; measure again",
};

/** 1.x data an upgraded install no longer reads; measured once per start unless refreshed. */
function Leftovers() {
  const client = useQueryClient();
  const view = useQuery({ queryKey: ["upgrade", "leftovers"], queryFn: () => body(api.GET("/api/v1/upgrade/leftovers")) });
  const measure = useMutation({
    mutationFn: () => body(api.POST("/api/v1/upgrade/leftovers/scan")),
    onSuccess: (next) => client.setQueryData(["upgrade", "leftovers"], next),
    // The server keeps a measurement whose request failed, so the next GET may have it.
    onError: () => client.invalidateQueries({ queryKey: ["upgrade", "leftovers"] }),
  });
  const v = view.data;
  const unmeasured = !!v?.imported && !v.scan && !v.scan_error;
  const { mutate, isIdle } = measure;
  useEffect(() => {
    if (unmeasured && isIdle) mutate();
  }, [unmeasured, isIdle, mutate]);
  if (!v?.imported || v.scan?.gone) return null;
  const scan = v.scan;
  const measured = scan?.items.filter((i) => i.status === "present") ?? [];
  const skipped = scan?.items.filter((i) => i.status === "not_assessed") ?? [];
  const partial = measured.some((i) => !i.complete);
  const paths = (items: Schemas["Leftover"][]) => (
    <ul className="mt-1.5 space-y-0.5 text-xs">
      {items.map((i) => (
        <li key={i.path} className="flex flex-wrap items-baseline justify-between gap-x-3">
          <span className="min-w-0 truncate font-mono" title={i.path}>
            {i.path}
          </span>
          <span className="text-muted-foreground tabular-nums">
            {i.reason ? notCounted[i.reason] : `${bytes(i.bytes ?? 0)}${i.complete ? "" : " or more"}`}
          </span>
        </li>
      ))}
    </ul>
  );
  return (
    <Card icon={Archive}>
      <div className="flex flex-wrap items-baseline justify-between gap-x-3">
        <span className="font-medium">infinidysk 1.x data</span>
        <span className="text-muted-foreground tabular-nums">
          {measure.isPending ? (
            <span className="flex items-center gap-1.5">
              <Loader2 className="text-primary size-3.5 animate-spin" />
              Measuring…
            </span>
          ) : scan ? (
            `${bytes(scan.bytes)} estimated disk usage`
          ) : (
            "–"
          )}
        </span>
      </div>
      {v.in_use ? (
        <p className="text-warning-text mt-0.5">Still in use. {v.in_use} Keep it until then.</p>
      ) : (
        measured.length > 0 && (
          <p className="text-muted-foreground mt-0.5">
            2.x no longer reads these. Once your library plays as expected, you can delete them to free disk space.
          </p>
        )
      )}
      {measured.length > 0 && paths(measured)}
      {skipped.length > 0 && (
        <>
          <p className="text-muted-foreground mt-1.5">Not measured, and not part of the estimate:</p>
          {paths(skipped)}
        </>
      )}
      <div className="text-muted-foreground mt-1.5 space-y-1 text-xs">
        <p>Paths inside this installation; in Docker, inside the container.</p>
        {partial && <p>Some of it could not be read, so the estimate is low.</p>}
        {v.source === "postgres" && (
          <p>1.x kept its database in PostgreSQL, which is not counted here. Drop it on your PostgreSQL server once you no longer need 1.x.</p>
        )}
        {v.scan_error && (
          <p className="text-warning-text">
            Measuring failed {v.scan_failed_at_ms ? stamp(v.scan_failed_at_ms) : ""}: {v.scan_error}
          </p>
        )}
        {measure.isError && <Failure error={measure.error} />}
        <p className="flex flex-wrap items-center gap-x-3">
          {scan && <span>Measured {ago(scan.at_ms)}</span>}
          <Button variant="link" size="sm" className="h-auto p-0 text-xs" disabled={measure.isPending} onClick={() => measure.mutate()}>
            Measure again
          </Button>
        </p>
      </div>
    </Card>
  );
}

const cores = (n: number) => (Number.isInteger(n) ? String(n) : n.toFixed(1));
const share = (part: number, whole: number) => (whole > 0 ? Math.min((part / whole) * 100, 100) : 0);
const percent = (part: number, whole: number) => `${share(part, whole)}%`;

function Resources({ live }: { live?: Schemas["LiveView"] }) {
  const r = live?.resources;
  if (!r) {
    const reason = live?.resources_unavailable;
    return (
      <>
        {([
          [Cpu, "CPU"],
          [MemoryStick, "Memory"],
        ] as const).map(([icon, title]) => (
          <Card key={title} icon={icon}>
            <div className="font-medium">{title}</div>
            <p className="text-muted-foreground mt-0.5">{live ? `Unavailable${reason ? `: ${reason}` : ""}` : "–"}</p>
          </Card>
        ))}
      </>
    );
  }
  const of = `of ${bytes(r.memory_limit_bytes)} ${r.memory_limited ? "limit" : "host"}`;
  const app = r.memory_process_bytes - r.memory_segment_cache_bytes;
  const budget = r.memory_segment_cache_limit_bytes;
  const memory = [
    {
      label: "App",
      tone: "bg-primary",
      size: app,
      hint: "Connections, read buffers and everything else infinidysk holds.",
    },
    {
      label: "Segment cache",
      tone: "bg-primary/45",
      size: r.memory_segment_cache_bytes,
      limit: budget,
      hint: "Decoded segments kept in memory so repeat reads skip the provider. The tick marks where it ends when full.",
    },
    {
      label: "Kernel cache",
      tone: "bg-primary/20",
      size: r.memory_kernel_cache_bytes,
      hint: "File contents and file metadata the kernel keeps cached. It may free this under memory pressure, but it still counts toward a container memory limit.",
    },
  ];
  return (
    <>
      <Card icon={Cpu}>
        <div className="flex items-baseline justify-between gap-3">
          <span className="font-medium">CPU</span>
          <span className="text-muted-foreground tabular-nums">
            {r.cpu_cores == null ? "–" : r.cpu_cores.toFixed(r.cpu_cores < 1 ? 2 : 1)} of {cores(r.cpu_limit_cores)} cores
          </span>
        </div>
        <div
          className="bg-muted mt-2 h-1.5 overflow-hidden rounded-full"
          role="progressbar"
          aria-label="CPU use"
          aria-valuemin={0}
          aria-valuemax={100}
          aria-valuenow={Math.round(share(r.cpu_cores ?? 0, r.cpu_limit_cores))}
        >
          <div className="bg-primary h-full" style={{ width: percent(r.cpu_cores ?? 0, r.cpu_limit_cores) }} />
        </div>
      </Card>
      <Card icon={MemoryStick}>
        <div className="flex items-baseline justify-between gap-3">
          <span className="font-medium">Memory</span>
          <span className="text-muted-foreground tabular-nums">
            {bytes(r.memory_process_bytes)} {of}
          </span>
        </div>
        <div className="relative mt-2">
          <div
            className="bg-muted flex h-1.5 overflow-hidden rounded-full"
            role="progressbar"
            aria-label="Memory use"
            aria-valuemin={0}
            aria-valuemax={100}
            aria-valuenow={Math.round(
              share(r.memory_process_bytes + r.memory_kernel_cache_bytes, r.memory_limit_bytes),
            )}
          >
            {memory.map((m) => (
              <div key={m.label} className={cn(m.tone, "h-full")} style={{ width: percent(m.size, r.memory_limit_bytes) }} />
            ))}
          </div>
          {budget > 0 && (
            <div
              aria-hidden
              className="bg-foreground/60 absolute -top-0.5 h-2.5 w-px"
              style={{ left: percent(app + budget, r.memory_limit_bytes) }}
            />
          )}
        </div>
        <div className="text-muted-foreground mt-1.5 flex flex-wrap gap-x-4 gap-y-1 text-xs tabular-nums">
          {memory.map((m) => (
            <span key={m.label} className="flex items-center gap-1.5">
              <span className={cn(m.tone, "size-2 rounded-full")} aria-hidden />
              {m.label} {bytes(m.size)}
              {m.limit ? ` of ${bytes(m.limit)}` : m.limit === 0 ? " (off)" : ""}
              <InfoTip label={`About ${m.label.toLowerCase()}`}>{m.hint}</InfoTip>
            </span>
          ))}
        </div>
      </Card>
    </>
  );
}

function DiskCache({ cache: c }: { cache: Schemas["DiskCacheStatus"] }) {
  const dir = (
    <span className="text-muted-foreground truncate font-mono text-xs" title={c.dir}>
      {c.dir}
    </span>
  );
  if (c.reason) {
    return (
      <Card icon={Database}>
        <div className="flex flex-wrap items-center gap-x-3">
          <Status tone="warning">
            <span className="font-medium">Disk cache not running</span>
          </Status>
          {dir}
        </div>
        <p className="text-muted-foreground mt-0.5">
          The directory is not writable: {c.reason}. Fix its permissions or pick another in{" "}
          <Link to="/settings/performance" className="text-primary relative underline-offset-4 after:absolute after:-inset-y-1 after:inset-x-0 hover:underline">
            Performance settings
          </Link>
          .
        </p>
      </Card>
    );
  }
  const h = c.max_age_hours;
  const age = h % 24 ? count(h, "hour") : count(h / 24, "day");
  return (
    <Card icon={Database}>
      <div className="flex items-baseline justify-between gap-3">
        <span className="flex min-w-0 items-baseline gap-3">
          <span className="shrink-0 font-medium">Disk cache</span>
          {dir}
        </span>
        <span className="text-muted-foreground shrink-0 tabular-nums">
          {bytes(c.used_bytes)} of {bytes(c.capacity_bytes)}
        </span>
      </div>
      <div
        className="bg-muted mt-2 h-1.5 overflow-hidden rounded-full"
        role="progressbar"
        aria-label="Disk cache use"
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={Math.round(share(c.used_bytes, c.capacity_bytes))}
      >
        <div className="bg-primary h-full" style={{ width: percent(c.used_bytes, c.capacity_bytes) }} />
      </div>
      <div className="text-muted-foreground mt-1.5 space-y-1 text-xs tabular-nums">
        <p>
          {count(c.segments, "segment")}
          {c.free_bytes != null && ` · ${bytes(c.free_bytes)} free on the volume`}
        </p>
        <p className="flex items-center gap-1.5">
          Since start: {count(c.hits, "hit")} · {bytes(c.written_bytes)} written · {bytes(c.evicted_bytes)} evicted
          <InfoTip label="About evictions">
            When the cache is full the least recently read segments go first, and segments not read for {age} are deleted.
          </InfoTip>
        </p>
        {c.indexing && (
          <p className="flex items-center gap-1.5">
            <Loader2 className="text-primary size-3.5 animate-spin" />
            Indexing segments from the last run…
          </p>
        )}
        {c.capacity_bytes < c.limit_bytes && (
          <p className="text-warning-text">Lowered from {bytes(c.limit_bytes)} to keep 1 GB free on the volume.</p>
        )}
        {c.skipped_writes > 0 && (
          <p className="flex items-center gap-1.5">
            {count(c.skipped_writes, "segment")} not cached while the disk was busy
            <InfoTip label="About skipped segments">
              The disk wrote slower than segments arrived, so these were served without being kept; a faster disk keeps more.
            </InfoTip>
          </p>
        )}
        {c.failed_writes > 0 && (
          <p className="text-warning-text">
            {count(c.failed_writes, "write")} failed. Check the directory’s free space and permissions.
          </p>
        )}
      </div>
    </Card>
  );
}

function Logs() {
  const [level, setLevel] = useState<Level>("info");
  const [filter, setFilter] = useState("");
  const [limit, setLimit] = useState(LOG_ROWS);
  const [params, setParams] = useSearchParams();
  const task = params.get("task") ?? undefined;
  const search = useDeferredValue(filter.trim().toLowerCase());
  const logs = useQuery({
    queryKey: ["logs", level, task],
    queryFn: () => body(api.GET("/api/v1/logs", { params: { query: { level, task } } })),
    refetchInterval: 3000,
    placeholderData: keepPreviousData,
  });
  const shown = (logs.data ?? [])
    .filter((e) => !search || `${e.message} ${e.fields} ${e.target}`.toLowerCase().includes(search))
    .reverse();

  return (
    <section>
      <div className="mb-3 flex flex-wrap items-center gap-2">
        <h2 className="mr-auto font-medium">Logs</h2>
        <Select
          value={task ?? "all"}
          onValueChange={(v) =>
            setParams(
              (p) => {
                if (v === "all") p.delete("task");
                else p.set("task", v);
                return p;
              },
              { replace: true },
            )
          }
        >
          <SelectTrigger aria-label="Task" className="w-full sm:w-52">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="all">All events</SelectItem>
            {Object.entries(jobCopy).map(([job, copy]) => (
              <SelectItem key={job} value={job}>
                {copy.title}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <div className="relative w-full sm:w-56">
          <Search className="text-muted-foreground pointer-events-none absolute top-1/2 left-2.5 size-3.5 -translate-y-1/2" aria-hidden />
          <Input
            type="search"
            className="pl-8"
            placeholder="Filter"
            aria-label="Filter logs"
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
          />
        </div>
        <ToggleGroup
          type="single"
          variant="outline"
          aria-label="Level"
          value={level}
          onValueChange={(value) => value && setLevel(value as Level)}
        >
          {levels.map((l) => (
            <ToggleGroupItem key={l.level} value={l.level} className="data-[state=on]:bg-primary/15 data-[state=on]:text-primary px-3">
              {l.label}
            </ToggleGroupItem>
          ))}
        </ToggleGroup>
      </div>
      {logs.isPending ? (
        <Loading />
      ) : logs.isError ? (
        <Failure error={logs.error} />
      ) : shown.length === 0 ? (
        <Empty title="Nothing logged">{search ? "No entries match the filter." : "Entries at this level show up here as they happen."}</Empty>
      ) : (
        <>
        <ul className="bg-card divide-y rounded-lg border font-mono text-xs">
          {shown.slice(0, limit).map((e, i) => (
            <Fragment key={`${e.time}-${i}`}>
              <li className="hover:bg-muted/40 flex flex-wrap gap-x-3 px-3 py-1.5 sm:flex-nowrap">
                <time className="text-muted-foreground shrink-0 tabular-nums" title={stamp(e.time)}>
                  <span className="sm:hidden">{new Date(e.time).toLocaleTimeString()}</span>
                  <span className="hidden sm:inline">{stamp(e.time)}</span>
                </time>
                <span className={cn("w-10 shrink-0 uppercase", tone[e.level])}>{e.level}</span>
                <div className="min-w-0 flex-1 basis-full break-words sm:basis-auto">
                  <span>{e.message}</span>
                  {e.task && !task && (
                    <Badge variant="outline" className="ml-1.5 max-w-full align-middle font-sans">
                      {jobCopy[e.task as keyof typeof jobCopy]?.title ?? e.task}
                    </Badge>
                  )}
                  {e.fields && <span className="text-muted-foreground"> {e.fields}</span>}
                  <span className="text-muted-foreground"> · {e.target}</span>
                </div>
              </li>
              {i + 1 < Math.min(limit, shown.length) && shown[i + 1]?.run !== e.run && (
                <li className="bg-muted/40 text-muted-foreground flex items-center gap-2 px-3 py-1.5 font-sans">
                  <span className="bg-primary size-1.5 rounded-full" />
                  <span className="text-foreground font-medium">Restarted</span>
                  {e.run && <time className="tabular-nums">{stamp(e.run)}</time>}
                </li>
              )}
            </Fragment>
          ))}
        </ul>
        {shown.length > LOG_ROWS && (
          <div className="text-muted-foreground mt-3 flex items-center justify-end gap-2 text-xs">
            <span className="tabular-nums">
              {Math.min(limit, shown.length)} of {shown.length}
            </span>
            {limit > LOG_ROWS && (
              <Button variant="ghost" size="sm" onClick={() => setLimit(LOG_ROWS)}>
                Show fewer
              </Button>
            )}
            {limit < shown.length && (
              <Button variant="outline" size="sm" onClick={() => setLimit((n) => n + LOG_ROWS)}>
                Show more
              </Button>
            )}
          </div>
        )}
        </>
      )}
    </section>
  );
}
