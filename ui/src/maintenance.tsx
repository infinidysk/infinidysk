import { useState } from "react";
import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowRight, CircleAlert, Loader2, ScrollText, TriangleAlert } from "lucide-react";
import { Link } from "react-router";
import { api, body, type Schemas } from "./api/client";
import { ago, bytes, duration, stamp } from "./format";
import type { Settings as Doc } from "./settings";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Switch } from "@/components/ui/switch";
import { ConfirmButton, Failure, Field, InfoTip, PAGE, Pager, Panel, Status, TextConfirmDialog, type Tone } from "./ui";

type Job = Schemas["MaintenanceJob"];
type View = Schemas["MaintenanceView"];
type Report = Schemas["MaintenanceReport"];
type Outcome = Schemas["RunOutcome"];

export const jobCopy: Record<Job, { title: string; blurb: string; tip: string; confirm: string; noun: string }> = {
  "backup-database": {
    title: "Back up database",
    blurb: "Writes a copy of the library database to the backup folder and keeps the newest copies.",
    tip: "Copies the library database (settings, logins, downloads, history and file details) while infinidysk keeps running. Each copy is checked and gets a SHA-256 checksum. After a good copy, the oldest scheduled and manual backups beyond Copies to keep are deleted; pinned, uploaded and added backups stay. It stops without deleting anything if the backup folder lacks room for the copy plus a reserve. It runs first in the window, before the jobs that delete. A PostgreSQL database is not backed up; use pg_dump.",
    confirm: "Writes a copy about the size of the database's used space. Imports and playback keep running.",
    noun: "backup",
  },
  "remove-unlinked": {
    title: "Remove unlinked files",
    blurb: "Deletes files that no symlink or STRM file in your library points to.",
    tip: "A file is unlinked when no symlink or STRM file in your library folders points to it and it is no longer in the download history. Files you only play from the mount or WebDAV count as unlinked. A dry run lists what would go without deleting anything. The job stops without deleting if a library folder is empty or unreadable, or if an Arr root folder is outside your library folders. If it would remove more than 90% of files, it waits for you to review the list.",
    confirm: "Files it finds are deleted from infinidysk and cannot be brought back. Run a dry run first to see the list.",
    noun: "file",
  },
  "remove-dead-links": {
    title: "Remove dead links",
    blurb: "Removes library symlinks and STRM files whose file is gone, and has the Arr search again.",
    tip: "A link is dead when the file it points to was deleted from infinidysk, for example by a health check. The job removes the link, then has each Sonarr or Radarr that lists it drop that file and search for its monitored episodes or movie. Searches count toward the same limit health checks use; a link whose media was searched too often lately waits for a later run. It stops without changing anything if a library folder is unreadable or an Arr does not answer. If more than 5% of links are dead, it waits for you to review the list.",
    confirm: "Dead links it finds are removed and the Arrs search for their media again. Run a dry run first to see the list.",
    noun: "link",
  },
  "compact-database": {
    title: "Compact database",
    blurb: "Gives space the database no longer uses back to the disk.",
    tip: "Runs in every window once at least 25% and 100 MB of a database file can be reclaimed; Run now reclaims any amount. It covers the library database and the metrics store, and skips a file when its disk lacks room for twice its size or the temp folder lacks room for its size. It waits while imports run, and writes wait while it runs. A PostgreSQL database is skipped; PostgreSQL reclaims space itself.",
    confirm: "Imports and other writes wait until it finishes, which can take a minute on a large database.",
    noun: "database",
  },
};

const databaseNames: Record<string, string> = { library: "Library database", metrics: "Metrics store" };

const outcomes: Record<Outcome, [string, Tone]> = {
  finished: ["Finished", "success"],
  held: ["Waiting for review", "warning"],
  stopped: ["Stopped", "destructive"],
  cancelled: ["Cancelled", "muted"],
  interrupted: ["Window ended", "muted"],
};

const triggers: Record<Schemas["RunTrigger"], string> = { manual: "Manual", window: "Scheduled", review: "Review" };

const plural = (n: number, word: string) => `${n.toLocaleString()} ${word}${n === 1 ? "" : "s"}`;

export function useMaintenance() {
  return useQuery({
    queryKey: ["maintenance"],
    queryFn: () => body(api.GET("/api/v1/maintenance")),
    refetchInterval: (q) => (q.state.data?.running ? 2000 : 30_000),
  });
}

/** Whether the report's files can be removed from the review dialog. */
const reviewable = (r: Report) => r.unlinked > 0 && (r.outcome === "held" || (r.dry_run && r.outcome === "finished"));

/** `done / total` of a running job; backup work counts bytes. */
export function progress(job: Job, live: Schemas["LiveMaintenance"]): string {
  const n = job === "backup-database" ? (v: number) => bytes(v) : (v: number) => v.toLocaleString();
  return live.total ? `${n(live.done)} / ${n(live.total)}` : live.done ? n(live.done) : "";
}

function summary(r: Report): string {
  if (r.outcome === "stopped") return r.reason ? "The run stopped early:" : "A safety check failed.";
  if (r.job === "backup-database") {
    const b = r.backup;
    if (!b) return "No backup was written.";
    const wrote = `Wrote ${b.name} (${bytes(b.size_bytes)}) in ${duration(Math.max(1, Math.round(b.duration_ms / 1000)))}.`;
    return b.pruned > 0 ? `${wrote} Removed ${plural(b.pruned, "old backup")} (${bytes(b.pruned_bytes)}).` : wrote;
  }
  if (r.job === "compact-database") {
    const lines = r.databases.map((d) => {
      const name = databaseNames[d.name] ?? d.name;
      if (d.skipped_reason) return `${name}: skipped, ${d.skipped_reason}.`;
      const reclaimed = d.size_bytes_before - (d.size_bytes_after ?? d.size_bytes_before);
      return `${name}: reclaimed ${bytes(reclaimed)} in ${((d.duration_ms ?? 0) / 1000).toFixed(1)} s.`;
    });
    return lines.join(" ") || "Nothing to compact.";
  }
  if (r.job === "remove-dead-links") {
    if (r.outcome === "held")
      return `Found ${plural(r.unlinked, "dead link")} out of ${plural(r.checked, "link")}. That is more than 5%, so nothing was removed.`;
    if (r.dry_run) return r.unlinked ? `Would remove ${plural(r.unlinked, "dead link")}.` : "No dead links.";
    const parts = [r.removed ? `Removed ${plural(r.removed, "dead link")}` : "Removed nothing"];
    if (r.skipped_open) parts.push(`${plural(r.skipped_open, "link")} left for a later run: searched too often lately`);
    return `${parts.join(", ")}.`;
  }
  if (r.outcome === "held")
    return `Found ${plural(r.unlinked, "unlinked file")} out of ${r.checked.toLocaleString()} checked. That is more than 90%, so nothing was removed.`;
  if (r.dry_run) return r.unlinked ? `Would remove ${plural(r.unlinked, "file")} (${bytes(r.unlinked_bytes)}).` : "Nothing to remove.";
  const parts = [r.removed ? `Removed ${plural(r.removed, "file")} (${bytes(r.removed_bytes)})` : "Removed nothing"];
  if (r.skipped_open) parts.push(`${plural(r.skipped_open, "open file")} left for next time`);
  if (r.folders_removed) parts.push(`${plural(r.folders_removed, "empty folder")} removed`);
  return `${parts.join(", ")}.`;
}

/** The maintenance settings page: the window, each job, and recent runs. */
export function MaintenancePanel({
  value,
  locked,
  onChange,
}: {
  value: Doc["maintenance"];
  locked: (path: string) => boolean;
  onChange: (value: Doc["maintenance"]) => void;
}) {
  const view = useMaintenance();
  const w = view.data?.window;
  // `*_local` is server wall-clock time; read it as UTC so the browser's zone does not shift it.
  const clock = (local: string, weekday: boolean) =>
    new Date(`${local}Z`).toLocaleString([], { weekday: weekday ? "short" : undefined, hour: "2-digit", minute: "2-digit", timeZone: "UTC" });
  const sameDay = w && w.start_local.slice(0, 10) === w.end_local.slice(0, 10);
  return (
    <div className="space-y-8">
      <Field
        label="Daily window"
        hint={
          <>
            Times are in {view.data ? `${view.data.time_zone}, ` : ""}the server's time zone. Scheduled jobs run one at a time, in the order
            below. An end before the start runs past midnight.
            {w &&
              (w.active
                ? ` Open now until ${clock(w.end_local, !sameDay)}.`
                : ` Next: ${clock(w.start_local, true)} to ${clock(w.end_local, !sameDay)}.`)}
          </>
        }
      >
        <div className="flex items-center gap-2">
          <Input
            type="time"
            aria-label="Window start"
            className="w-40"
            required
            disabled={locked("maintenance.window")}
            value={value.window.start}
            onChange={(e) => onChange({ ...value, window: { ...value.window, start: e.target.value } })}
          />
          <span className="text-muted-foreground">to</span>
          <Input
            type="time"
            aria-label="Window end"
            className="w-40"
            required
            disabled={locked("maintenance.window")}
            value={value.window.end}
            onChange={(e) => onChange({ ...value, window: { ...value.window, end: e.target.value } })}
          />
        </div>
      </Field>

      <div className="space-y-3">
        <h3 className="font-medium">Jobs</h3>
        {(view.data?.jobs ?? []).map((j, i) =>
          j.job === "backup-database" ? (
            <BackupStep
              key={j.job}
              job={j}
              step={i + 1}
              running={view.data?.running ?? null}
              scheduled={j.always_scheduled || value.scheduled.includes(j.job)}
            />
          ) : (
            <JobCard
              key={j.job}
              job={j}
              step={i + 1}
              running={view.data?.running ?? null}
              scheduled={j.always_scheduled || value.scheduled.includes(j.job)}
              locked={locked("maintenance.scheduled")}
              onScheduled={(on) =>
                onChange({ ...value, scheduled: on ? [...value.scheduled, j.job] : value.scheduled.filter((s) => s !== j.job) })
              }
            />
          ),
        )}
        {view.isError && <p className="text-destructive">{view.error.message}</p>}
      </div>

      <Runs runs={view.data?.runs ?? []} />
    </div>
  );
}

/** The backup job's place in the window order; its controls live on Settings → Backups. */
function BackupStep({
  job,
  step,
  running,
  scheduled,
}: {
  job: Schemas["MaintenanceJobView"];
  step: number;
  running: Schemas["LiveMaintenance"] | null;
  scheduled: boolean;
}) {
  const mine = running?.job === job.job ? running : null;
  const r = job.report;
  return (
    <div className="bg-card flex flex-wrap items-center justify-between gap-x-4 gap-y-2 rounded-lg border p-4">
      <div className="min-w-0">
        <div className="flex items-center gap-1.5 font-medium">
          <StepNumber step={step} />
          {jobCopy[job.job].title}
        </div>
        <p className="text-muted-foreground mt-0.5 flex flex-wrap items-center gap-x-2 gap-y-1 text-xs">
          {mine ? (
            <span className="flex items-center gap-1.5">
              <Loader2 className="text-primary size-3.5 animate-spin" />
              {mine.step} {progress(job.job, mine)}
            </span>
          ) : (
            <>
              {job.unavailable_reason ? (
                <span className="text-warning-text">{job.unavailable_reason}</span>
              ) : (
                <span>{job.always_scheduled ? "Always runs daily" : scheduled ? "Runs daily" : "Not scheduled"}</span>
              )}
              {r && (
                <>
                  <span aria-hidden>·</span>
                  <Status tone={outcomes[r.outcome][1]}>{outcomes[r.outcome][0]}</Status>
                  <span title={stamp(r.finished_ms)}>{ago(r.finished_ms)}</span>
                </>
              )}
            </>
          )}
        </p>
      </div>
      <Button type="button" variant="outline" size="sm" asChild>
        <Link to="/settings/backups">
          Backups <ArrowRight />
        </Link>
      </Button>
    </div>
  );
}

function StepNumber({ step }: { step: number }) {
  return (
    <span className="bg-muted text-muted-foreground mr-0.5 inline-flex size-5 shrink-0 items-center justify-center rounded text-xs tabular-nums">
      {step}
    </span>
  );
}

export function JobCard({
  job,
  step,
  running,
  scheduled,
  locked,
  onScheduled,
}: {
  job: Schemas["MaintenanceJobView"];
  /** Its place in the window's run order. */
  step?: number;
  running: Schemas["LiveMaintenance"] | null;
  scheduled: boolean;
  locked: boolean;
  onScheduled: (on: boolean) => void;
}) {
  const client = useQueryClient();
  const [reviewing, setReviewing] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const copy = jobCopy[job.job];
  const mine = running?.job === job.job ? running : null;
  const start = useMutation({
    mutationFn: (dry_run: boolean) =>
      body(api.POST("/api/v1/maintenance/{job}/run", { params: { path: { job: job.job } }, body: { dry_run } })),
    onSuccess: (view) => client.setQueryData<View>(["maintenance"], view),
  });
  const cancel = useMutation({
    mutationFn: () => body(api.POST("/api/v1/maintenance/cancel")),
    onSettled: () => void client.invalidateQueries({ queryKey: ["maintenance"] }),
  });
  const r = job.report;
  const busy = !!running || start.isPending;
  const id = `job-${job.job}`;

  return (
    <div className="bg-card rounded-lg border">
      <div className="flex items-start justify-between gap-4 p-4">
        <div className="min-w-0">
          <div className="flex items-center gap-1.5 font-medium">
            {step != null && <StepNumber step={step} />}
            {copy.title}
            <InfoTip label={`About ${copy.title}`}>{copy.tip}</InfoTip>
          </div>
          <p className="text-muted-foreground mt-0.5 text-xs">{copy.blurb}</p>
        </div>
        {job.always_scheduled ? (
          <span className="text-muted-foreground shrink-0 text-xs">Always runs daily</span>
        ) : (
          <label htmlFor={id} className="text-muted-foreground flex shrink-0 items-center gap-2 text-xs">
            Run daily
            <Switch id={id} checked={scheduled} disabled={locked || (!!job.unavailable_reason && !scheduled)} onCheckedChange={onScheduled} />
          </label>
        )}
      </div>

      <div className="space-y-3 border-t px-4 py-3">
        {mine ? (
          <div className="space-y-1.5">
            <div className="flex items-center justify-between gap-2 text-xs">
              <span className="flex items-center gap-2">
                <Loader2 className="text-primary size-3.5 animate-spin" />
                {mine.dry_run ? "Dry run: " : ""}
                {mine.step}
              </span>
              <span className="text-muted-foreground tabular-nums">
                {progress(job.job, mine)}
              </span>
            </div>
            <div className="bg-muted h-1.5 overflow-hidden rounded-full">
              <div
                className={mine.total ? "bg-primary h-full transition-[width]" : "bg-primary/40 h-full w-1/3 animate-pulse"}
                style={mine.total ? { width: `${(mine.done / mine.total) * 100}%` } : undefined}
              />
            </div>
          </div>
        ) : r ? (
          <div className="space-y-1 text-xs">
            <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
              <Status tone={outcomes[r.outcome][1]}>{outcomes[r.outcome][0]}</Status>
              {r.dry_run && <Badge variant="outline">Dry run</Badge>}
              <span className="text-muted-foreground" title={stamp(r.finished_ms)}>
                {ago(r.finished_ms)}
              </span>
            </div>
            <p className={r.outcome === "stopped" ? "text-destructive" : "text-muted-foreground"}>{summary(r)}</p>
            {r.outcome === "stopped" && r.reason && <p className="text-muted-foreground font-mono break-words">{r.reason}</p>}
            {r.warnings.map((w) => (
              <p key={w} className="text-warning-text flex items-start gap-1.5">
                <TriangleAlert className="mt-px size-3.5 shrink-0" />
                {w}
              </p>
            ))}
          </div>
        ) : (
          <p className="text-muted-foreground text-xs">{job.unavailable_reason ?? "Not run since the last restart."}</p>
        )}
        {start.isError && <Failure error={start.error} />}

        <div className="flex flex-wrap items-center gap-2">
          {mine ? (
            <Button type="button" variant="outline" size="sm" disabled={cancel.isPending} onClick={() => cancel.mutate()}>
              Cancel
            </Button>
          ) : (
            <>
              {job.has_dry_run && (
                <Button type="button" variant="outline" size="sm" disabled={busy || !!job.unavailable_reason} onClick={() => start.mutate(true)}>
                  Dry run
                </Button>
              )}
              {busy || job.unavailable_reason ? (
                // A disabled button gets no hover, so the reason sits on its wrapper.
                <span title={job.unavailable_reason ?? "Another job is running"}>
                  <Button type="button" variant="outline" size="sm" disabled>
                    Run now
                  </Button>
                </span>
              ) : job.job === "remove-unlinked" ? (
                <Button type="button" variant="outline" size="sm" onClick={() => setDeleting(true)}>
                  Run now
                </Button>
              ) : (
                <ConfirmButton
                  variant="outline"
                  label="Run now"
                  title={`${copy.title} now?`}
                  message={copy.confirm}
                  confirm="Run now"
                  onConfirm={() => start.mutate(false)}
                />
              )}
            </>
          )}
          {r && r.unlinked > 0 && !mine && (
            <Button type="button" variant={reviewable(r) ? "default" : "ghost"} size="sm" onClick={() => setReviewing(true)}>
              {reviewable(r) ? "Review" : "Files"}
            </Button>
          )}
          <Button type="button" variant="ghost" size="sm" className="ml-auto" asChild>
            <Link to={`/system?task=${job.job}`}>
              <ScrollText /> Logs
            </Link>
          </Button>
        </div>
      </div>
      {reviewing && <ReportDialog job={job.job} busy={busy} onClose={() => setReviewing(false)} />}
      <TextConfirmDialog
        open={deleting}
        onOpenChange={setDeleting}
        title="Delete unlinked files now?"
        message={copy.confirm}
        expected="delete"
        confirm="Delete unlinked files"
        onConfirm={() => start.mutate(false)}
      />
    </div>
  );
}

function ReportDialog({ job, busy, onClose }: { job: Job; busy: boolean; onClose: () => void }) {
  const client = useQueryClient();
  const [offset, setOffset] = useState(0);
  const page = useQuery({
    queryKey: ["maintenance", "report", job, offset],
    queryFn: () =>
      body(api.GET("/api/v1/maintenance/{job}/report", { params: { path: { job }, query: { offset, limit: PAGE } } })),
    placeholderData: keepPreviousData,
  });
  const done = (view?: View) => {
    if (view) client.setQueryData<View>(["maintenance"], view);
    else void client.invalidateQueries({ queryKey: ["maintenance"] });
    onClose();
  };
  const confirm = useMutation({
    mutationFn: (at_ms: number) => body(api.POST("/api/v1/maintenance/{job}/report/confirm", { params: { path: { job } }, body: { at_ms } })),
    onSuccess: (view) => done(view),
  });
  const dismiss = useMutation({
    mutationFn: () => body(api.DELETE("/api/v1/maintenance/{job}/report", { params: { path: { job } } })),
    onSuccess: () => done(),
  });
  const p = page.data;
  const r = p?.report;
  const links = job === "remove-dead-links";
  const noun = jobCopy[job].noun;

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent className="sm:max-w-3xl">
        <DialogHeader>
          <DialogTitle>{jobCopy[job].title}</DialogTitle>
          <DialogDescription>{r ? summary(r) : "Loading the report…"}</DialogDescription>
        </DialogHeader>
        {page.isError ? (
          <Alert variant="destructive">
            <CircleAlert />
            <AlertDescription>{page.error.message}</AlertDescription>
          </Alert>
        ) : p && r ? (
          <div className="min-w-0 space-y-3">
            <Panel className="max-h-[50vh] overflow-auto">
              <ul className="divide-y text-xs">
                {p.files.map((f) => (
                  <li key={f.path} className="flex items-center gap-3 px-3 py-1.5">
                    <span className="min-w-0 flex-1 truncate font-mono" title={f.path}>
                      {f.path}
                    </span>
                    {f.removed && <Badge variant="outline">Removed</Badge>}
                    {links ? (
                      <span className="text-muted-foreground max-w-40 shrink-0 truncate" title="Arrs that list it">
                        {f.arrs.join(", ") || "No Arr"}
                      </span>
                    ) : (
                      <span className="text-muted-foreground shrink-0 tabular-nums" title={`Added ${stamp(f.created_ms)}`}>
                        {bytes(f.size_bytes)}
                      </span>
                    )}
                  </li>
                ))}
              </ul>
            </Panel>
            <div className="flex items-center justify-between gap-2">
              <span className="text-muted-foreground text-xs tabular-nums">
                {plural(p.total, noun)}
                {!links && ` · ${bytes(r.unlinked_bytes)}`}
              </span>
              <Pager offset={offset} more={offset + PAGE < p.total} onChange={setOffset} />
            </div>
            {confirm.isError && <p className="text-destructive text-xs">{confirm.error.message}</p>}
          </div>
        ) : (
          <p className="text-muted-foreground flex items-center gap-2">
            <Loader2 className="size-4 animate-spin" /> Loading…
          </p>
        )}
        <DialogFooter>
          <Button variant="ghost" className="sm:mr-auto" disabled={dismiss.isPending} onClick={() => dismiss.mutate()}>
            Dismiss report
          </Button>
          <Button variant="outline" onClick={onClose}>
            Close
          </Button>
          {r && reviewable(r) && (
            <Button variant="destructive" disabled={busy || confirm.isPending} onClick={() => confirm.mutate(r.at_ms)}>
              {confirm.isPending && <Loader2 className="animate-spin" />}
              Remove {plural(r.unlinked, noun)}
            </Button>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function Runs({ runs }: { runs: Schemas["MaintenanceRun"][] }) {
  return (
    <div className="space-y-3">
      <h3 className="font-medium">Recent runs</h3>
      {runs.length === 0 ? (
        <p className="text-muted-foreground text-xs">No runs yet.</p>
      ) : (
        <Panel>
          <ul className="divide-y text-xs">
            {runs.map((run) => (
              <li key={`${run.job}-${run.started_ms}`} className="grid grid-cols-[1fr_auto] gap-x-3 gap-y-0.5 px-3 py-2 sm:grid-cols-[10rem_1fr_auto_auto]">
                <span className="text-muted-foreground tabular-nums" title={stamp(run.started_ms)}>
                  {ago(run.started_ms)}
                </span>
                <span className="order-first col-span-2 min-w-0 truncate sm:order-none sm:col-span-1">
                  {jobCopy[run.job].title}
                  <span className="text-muted-foreground">
                    {" "}
                    · {triggers[run.trigger]}
                    {run.dry_run && " · dry run"}
                  </span>
                </span>
                <span className="text-muted-foreground tabular-nums">
                  {!run.files
                    ? ""
                    : run.job === "backup-database"
                      ? `Wrote ${bytes(run.bytes)}`
                      : run.job === "compact-database"
                        ? `${plural(run.files, "database")} · ${bytes(run.bytes)} reclaimed`
                        : run.job === "remove-dead-links"
                          ? plural(run.files, "link")
                          : `${plural(run.files, "file")} · ${bytes(run.bytes)}`}
                </span>
                <span>
                  <Status tone={outcomes[run.outcome][1]}>{outcomes[run.outcome][0]}</Status>
                </span>
                {run.reason && <p className="text-muted-foreground col-span-full font-mono break-words">{run.reason}</p>}
              </li>
            ))}
          </ul>
        </Panel>
      )}
    </div>
  );
}

/** The running maintenance job in the sidebar, from the live view. */
export function MaintenanceNow({ live }: { live: Schemas["LiveMaintenance"] }) {
  const amount = live.total ? `${Math.round((live.done / live.total) * 100)}%` : progress(live.job, live);
  return (
    <Link
      to={live.job === "backup-database" ? "/settings/backups" : "/settings/maintenance"}
      className="hover:bg-sidebar-accent/60 flex items-center gap-2 rounded-md px-2 py-1.5 text-xs"
    >
      <Loader2 className="text-primary size-3.5 shrink-0 animate-spin" />
      <span className="min-w-0 flex-1 truncate" title={`${jobCopy[live.job].title} · ${live.step}`}>
        {jobCopy[live.job].title}
        <span className="text-muted-foreground"> · {live.step}</span>
      </span>
      {amount && <span className="text-muted-foreground tabular-nums">{amount}</span>}
    </Link>
  );
}
