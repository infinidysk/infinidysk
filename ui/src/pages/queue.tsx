import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Download, Info, Loader2, Pause, Play, RotateCcw, Search, Trash2, TriangleAlert, Upload } from "lucide-react";
import { type ReactNode, useRef, useState } from "react";
import { Link } from "react-router";
import { toast } from "sonner";
import { api, body, type Schemas } from "../api/client";
import { ago, bytes, duration, stamp } from "../format";
import { useSettings } from "../settings";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { Empty, Failure, IconButton, Loading, PAGE, PageHeader, Pager, Panel, ReleaseName, RowActions, Status } from "../ui";

const RING = 2 * Math.PI * 6;
const DAY_MS = 24 * 60 * 60 * 1000;

function Importing({ id }: { id: string }) {
  // Kept fresh by the `live` server-sent event every 2 s.
  const live = useQuery({
    queryKey: ["live"],
    queryFn: () => body(api.GET("/api/v1/live")),
    staleTime: Infinity,
  });
  const progress = live.data?.imports.find((i) => i.id === id);
  if (!progress) {
    return (
      <span className="text-primary inline-flex items-center gap-2">
        <Loader2 className="size-3.5 animate-spin" />
        Importing
      </span>
    );
  }
  const eta = progress.eta_ms == null ? null : duration(Math.max(1, Math.round(progress.eta_ms / 1000)));
  return (
    <span
      className="text-primary inline-flex items-center gap-2 tabular-nums"
      title={eta ? progress.step : `${progress.step}. Estimating time left`}
    >
      <svg
        viewBox="0 0 16 16"
        className="size-3.5 shrink-0 -rotate-90"
        role="progressbar"
        aria-label="Import progress"
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={progress.percent}
      >
        <circle cx="8" cy="8" r="6" fill="none" strokeWidth="2.5" className="stroke-muted" />
        <circle
          cx="8"
          cy="8"
          r="6"
          fill="none"
          strokeWidth="2.5"
          stroke="currentColor"
          strokeDasharray={RING}
          strokeDashoffset={RING * (1 - progress.percent / 100)}
          className="transition-[stroke-dashoffset] duration-500"
        />
      </svg>
      {progress.percent}%
      {eta && <span className="text-muted-foreground hidden sm:inline">· {eta} left</span>}
    </span>
  );
}

function JobStatus({ job }: { job: Schemas["QueueJob"] }) {
  switch (job.status) {
    case "importing":
      return <Importing id={job.id} />;
    case "waiting":
      return (
        <span title={job.retry_at ? `Retrying ${stamp(job.retry_at)}` : undefined}>
          <Status tone="warning" compact>
            Waiting
          </Status>
        </span>
      );
    case "paused":
      return (
        <Status tone="muted" compact>
          Paused
        </Status>
      );
    default:
      return (
        <Status tone="muted" compact>
          Queued
        </Status>
      );
  }
}

function Name({ name, href }: { name: string; href?: string }) {
  return (
    <TableCell className="w-full max-w-0">
      {href ? (
        <Link to={href} className="block truncate font-medium hover:underline" title={name}>
          <ReleaseName name={name} />
        </Link>
      ) : (
        <div className="truncate font-medium" title={name}>
          <ReleaseName name={name} />
        </div>
      )}
    </TableCell>
  );
}

/** A status followed by an icon that opens the full error, which is too long for the row. */
function ErrorDetails({
  status,
  title,
  name,
  message,
  note,
  onRetry,
}: {
  status: ReactNode;
  title: string;
  name: string;
  message: string;
  note?: string;
  onRetry?: () => void;
}) {
  const [open, setOpen] = useState(false);
  return (
    <span className="inline-flex items-center gap-1">
      {status}
      <IconButton size="icon-xs" className="text-muted-foreground" label="Show error" onClick={() => setOpen(true)}>
        <Info />
      </IconButton>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent className="sm:max-w-2xl">
          <DialogHeader>
            <DialogTitle>{title}</DialogTitle>
            <DialogDescription className="break-all">
              <ReleaseName name={name} />
            </DialogDescription>
          </DialogHeader>
          <div className="bg-muted/50 max-h-[60vh] overflow-auto rounded-md border p-3 font-mono text-xs wrap-anywhere whitespace-pre-wrap">
            {message}
          </div>
          {note && <p className="text-muted-foreground text-xs">{note}</p>}
          <DialogFooter>
            {onRetry && (
              <Button
                variant="outline"
                onClick={() => {
                  onRetry();
                  setOpen(false);
                }}
              >
                <RotateCcw />
                Retry
              </Button>
            )}
            <Button onClick={() => setOpen(false)}>Close</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </span>
  );
}

/** Picks NZB files and queues each under the chosen category. */
function AddNzbs() {
  const client = useQueryClient();
  const settings = useSettings();
  const input = useRef<HTMLInputElement>(null);
  const [category, setCategory] = useState<string>();
  const sab = settings.data?.settings.sab;
  // Radix Select throws on an empty item value, so blanks never reach it.
  const categories = sab
    ? [...new Set([sab.manual_category, ...sab.categories].map((c) => c.trim()).filter(Boolean))]
    : [];
  const chosen = category ?? categories[0];
  const upload = useMutation({
    mutationFn: async (files: File[]) => {
      let queued = 0;
      for (const file of files) {
        const form = new FormData();
        if (chosen) form.append("category", chosen);
        form.append("nzb", file);
        try {
          await body(api.POST("/api/v1/queue", { body: {} as Schemas["Upload"], bodySerializer: () => form }));
          queued += 1;
        } catch (error) {
          toast.error(`${file.name}: ${(error as Error).message}`);
        }
      }
      return queued;
    },
    onSuccess: (queued) => {
      if (queued > 0) toast.success(`Queued ${queued} ${queued === 1 ? "NZB" : "NZBs"} in ${chosen}`);
    },
    onSettled: () => client.invalidateQueries({ queryKey: ["queue"] }),
  });

  return (
    <div className="flex items-center gap-2">
      <Select value={chosen ?? ""} onValueChange={setCategory} disabled={!sab}>
        <SelectTrigger size="sm" className="max-w-56" aria-label="Category for new NZBs">
          <SelectValue placeholder="Category">
            {chosen && (
              <span className="truncate">
                <span className="text-muted-foreground">Category </span>
                {chosen}
              </span>
            )}
          </SelectValue>
        </SelectTrigger>
        <SelectContent>
          {categories.map((c) => (
            <SelectItem key={c} value={c}>
              {c}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>
      <Button size="sm" disabled={!sab || upload.isPending} onClick={() => input.current?.click()}>
        {upload.isPending ? <Loader2 className="animate-spin" /> : <Upload />}
        Add NZBs
      </Button>
      <input
        ref={input}
        type="file"
        accept=".nzb,.gz,application/x-nzb,application/gzip,application/octet-stream"
        multiple
        hidden
        onChange={(e) => {
          const files = Array.from(e.target.files ?? []);
          e.target.value = "";
          if (files.length > 0) upload.mutate(files);
        }}
      />
    </div>
  );
}

/** Finished downloads an Arr will not import for a reason only the user can settle. */
function ArrIssues() {
  const status = useQuery({
    queryKey: ["arr-status"],
    queryFn: () => body(api.GET("/api/v1/arr/status")),
    refetchInterval: 10000,
  });
  const issues = status.data?.instances.flatMap((i) => i.queue_issues.map((issue) => ({ arr: i.name, ...issue }))) ?? [];
  if (issues.length === 0) return null;
  return (
    <Alert className="mb-6">
      <TriangleAlert className="text-warning-text" />
      <AlertTitle>
        {issues.length === 1 ? "1 download needs" : `${issues.length} downloads need`} you in the Arr before it can import
      </AlertTitle>
      <AlertDescription>
        <ul className="w-full divide-y">
          {issues.map((issue, i) => (
            <li key={i} className="min-w-0 py-2">
              <div className="text-foreground truncate" title={issue.title}>
                <ReleaseName name={issue.title} />
                <span className="text-muted-foreground"> · {issue.arr}</span>
              </div>
              {issue.messages.map((m, j) => (
                <div key={j} className="truncate text-xs" title={m}>
                  {m}
                </div>
              ))}
            </li>
          ))}
        </ul>
      </AlertDescription>
    </Alert>
  );
}

/** Active jobs first, then finished ones, paged as one list. */
export function Queue() {
  const client = useQueryClient();
  const [offset, setOffset] = useState(0);
  const [search, setSearch] = useState("");
  const [status, setStatus] = useState<"any" | Schemas["HistoryStatus"]>("any");
  const term = search.trim();
  const filtered = term !== "" || status !== "any";
  const queue = useQuery({ queryKey: ["queue"], queryFn: () => body(api.GET("/api/v1/queue")) });
  const paused = useQuery({ queryKey: ["queue", "paused"], queryFn: () => body(api.GET("/api/v1/queue/paused")) });
  const setPaused = useMutation({
    mutationFn: (value: boolean) => body(api.PUT("/api/v1/queue/paused", { body: { paused: value } })),
    onSettled: () => client.invalidateQueries({ queryKey: ["queue"] }),
  });
  const pauseJob = useMutation({
    mutationFn: ({ id, value }: { id: string; value: boolean }) =>
      body(api.PUT("/api/v1/queue/{id}/paused", { params: { path: { id } }, body: { paused: value } })),
    onSettled: () => client.invalidateQueries({ queryKey: ["queue"] }),
  });
  // Finished jobs filter on the server; active ones only by name, and not at all once a finished status is picked.
  const queued =
    queue.data?.filter((job) => status === "any" && (!term || job.name.toLowerCase().includes(term.toLowerCase()))) ?? [];
  const active = queued.slice(offset, offset + PAGE);
  const historyOffset = Math.max(0, offset - queued.length);
  const historyLimit = PAGE - active.length;
  const historyQuery = {
    limit: historyLimit + 1,
    offset: historyOffset,
    search: term || undefined,
    status: status === "any" ? undefined : status,
    // Old failures stay in history; only the unfiltered view leaves them out.
    failed_max_age_ms: filtered ? undefined : DAY_MS,
  };
  const history = useQuery({
    queryKey: ["history", historyQuery],
    queryFn: () => body(api.GET("/api/v1/history", { params: { query: historyQuery } })),
    enabled: queue.isSuccess,
    placeholderData: keepPreviousData,
  });
  const removeJob = useMutation({
    mutationFn: (id: string) => body(api.DELETE("/api/v1/queue/{id}", { params: { path: { id } } })),
    onSettled: () => client.invalidateQueries({ queryKey: ["queue"] }),
  });
  const removeHistory = useMutation({
    mutationFn: (id: string) => body(api.DELETE("/api/v1/history/{id}", { params: { path: { id } } })),
    onSettled: () => client.invalidateQueries({ queryKey: ["history"] }),
  });
  const retry = useMutation({
    mutationFn: (id: string) => body(api.POST("/api/v1/history/{id}/retry", { params: { path: { id } } })),
    onSuccess: () => toast.success("Queued again"),
    onSettled: () => client.invalidateQueries({ queryKey: ["queue"] }),
  });
  const removeError = removeJob.error ?? removeHistory.error ?? retry.error ?? setPaused.error ?? pauseJob.error;
  const finished = history.data?.slice(0, historyLimit) ?? [];
  const more = queued.length > offset + PAGE || (history.data?.length ?? 0) > historyLimit;
  const blank = offset === 0 && active.length === 0 && finished.length === 0 && !filtered;
  // An empty default view can still hide old failures; the filters must stay reachable then.
  const anyHistory = useQuery({
    queryKey: ["history", { limit: 1 }],
    queryFn: () => body(api.GET("/api/v1/history", { params: { query: { limit: 1 } } })),
    enabled: blank && history.isSuccess,
  });
  const error = queue.error ?? history.error ?? anyHistory.error;
  const clearFilters = () => {
    setSearch("");
    setStatus("any");
    setOffset(0);
  };

  return (
    <>
      <PageHeader
        title="Queue"
        description="NZBs importing now, then finished imports, newest first. Failures older than a day show under the Failed filter."
      >
        {queue.data && queue.data.length > 0 && (
          <span className="text-muted-foreground whitespace-nowrap tabular-nums">
            {queue.data.length} {queue.data.length === 1 ? "job" : "jobs"} ·{" "}
            {bytes(queue.data.reduce((sum, job) => sum + job.total_bytes, 0))}
          </span>
        )}
        {paused.data && (
          <Button
            size="sm"
            variant="outline"
            disabled={setPaused.isPending}
            onClick={() => setPaused.mutate(!paused.data.paused)}
          >
            {paused.data.paused ? <Play /> : <Pause />}
            {paused.data.paused ? "Resume queue" : "Pause queue"}
          </Button>
        )}
        <AddNzbs />
      </PageHeader>
      {paused.data?.paused && (
        <Alert className="mb-6">
          <TriangleAlert className="text-warning-text" />
          <AlertTitle>The queue is paused</AlertTitle>
          <AlertDescription>No new import starts until you resume it; running imports finish.</AlertDescription>
        </Alert>
      )}
      {removeError && <Failure error={removeError} className="mb-6" />}
      <ArrIssues />
      {error ? (
        <Failure error={error} />
      ) : !queue.data || !history.data || (blank && !anyHistory.data) ? (
        <Loading />
      ) : blank && anyHistory.data?.length === 0 ? (
        <Empty title="No imports yet">NZBs sent by Sonarr, Radarr and the other Arrs show up here while they import. Use Add NZBs to queue one yourself.</Empty>
      ) : (
        <>
          <div className="mb-3 flex flex-wrap items-center gap-2">
            <div className="relative min-w-40 flex-1 sm:max-w-80">
              <Search className="text-muted-foreground pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2" aria-hidden />
              <Input
                className="pl-8"
                placeholder="Search names"
                aria-label="Search names"
                value={search}
                onChange={(e) => {
                  setSearch(e.target.value);
                  setOffset(0);
                }}
              />
            </div>
            <Select
              value={status}
              onValueChange={(v) => {
                setStatus(v as typeof status);
                setOffset(0);
              }}
            >
              <SelectTrigger className="w-36" aria-label="Status">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="any">Any status</SelectItem>
                <SelectItem value="completed">Completed</SelectItem>
                <SelectItem value="failed">Failed</SelectItem>
              </SelectContent>
            </Select>
          </div>
          {blank ? (
            <div className="rounded-lg border border-dashed px-6 py-10 text-center">
              <p className="font-medium">Nothing in the last day</p>
              <p className="text-muted-foreground mt-1">Older failed imports are still in history.</p>
              <Button variant="outline" size="sm" className="mt-4" onClick={() => setStatus("failed")}>
                Show failed imports
              </Button>
            </div>
          ) : offset === 0 && active.length === 0 && finished.length === 0 ? (
            <div className="rounded-lg border border-dashed px-6 py-10 text-center">
              <p className="font-medium">No results found</p>
              <p className="text-muted-foreground mt-1">{term ? <>No imports match “{term}” with this status.</> : "No imports have this status."}</p>
              <Button variant="outline" size="sm" className="mt-4" onClick={clearFilters}>
                Clear filters
              </Button>
            </div>
          ) : (
            <>
              <Panel>
                <Table>
                  <TableHeader>
                    <TableRow>
                      <TableHead>Name</TableHead>
                      <TableHead className="hidden w-32 md:table-cell">Category</TableHead>
                      <TableHead className="hidden w-24 text-right sm:table-cell">Size</TableHead>
                      <TableHead>
                        <span className="sr-only sm:not-sr-only">Status</span>
                      </TableHead>
                      <TableHead className="hidden w-24 text-right lg:table-cell">Took</TableHead>
                      <TableHead className="hidden w-28 sm:table-cell">Time</TableHead>
                      <TableHead className="w-20">
                        <span className="sr-only">Actions</span>
                      </TableHead>
                    </TableRow>
                  </TableHeader>
                  <TableBody>
                    {active.map((job) => (
                      <TableRow key={job.id}>
                        <Name name={job.name} />
                        <TableCell className="text-muted-foreground hidden md:table-cell">{job.category}</TableCell>
                        <TableCell className="hidden text-right tabular-nums sm:table-cell">{bytes(job.total_bytes)}</TableCell>
                        <TableCell>
                          {job.last_error && job.status !== "importing" ? (
                            <ErrorDetails
                              status={<JobStatus job={job} />}
                              title="Last import attempt failed"
                              name={job.name}
                              message={job.last_error}
                              note={
                                [
                                  job.attempts > 1 && `${job.attempts} attempts`,
                                  job.stuck_count > 0 && `stalled ${job.stuck_count === 1 ? "once" : `${job.stuck_count} times`}`,
                                  job.retry_at && `next try ${stamp(job.retry_at)}`,
                                ]
                                  .filter(Boolean)
                                  .join(" · ") || undefined
                              }
                            />
                          ) : (
                            <JobStatus job={job} />
                          )}
                        </TableCell>
                        <TableCell className="text-muted-foreground hidden text-right lg:table-cell">–</TableCell>
                        <TableCell className="text-muted-foreground hidden sm:table-cell" title={`Added ${stamp(job.created_at)}`}>
                          {ago(job.created_at)}
                        </TableCell>
                        <TableCell>
                          <RowActions
                            name={job.name}
                            actions={[
                              job.status !== "importing" && {
                                label: job.status === "paused" ? "Resume" : "Pause",
                                icon: job.status === "paused" ? <Play /> : <Pause />,
                                disabled: pauseJob.isPending,
                                onSelect: () => pauseJob.mutate({ id: job.id, value: job.status !== "paused" }),
                              },
                              { label: "Download NZB", icon: <Download />, href: `/api/v1/queue/${job.id}/nzb` },
                              {
                                label: "Remove",
                                icon: <Trash2 />,
                                onSelect: () => removeJob.mutate(job.id),
                                confirm: { title: "Remove from queue?", message: job.name, confirm: "Remove" },
                              },
                            ]}
                          />
                        </TableCell>
                      </TableRow>
                    ))}
                    {finished.map((job) => (
                      <TableRow key={job.id}>
                        <Name
                          name={job.name}
                          href={job.status === "completed" && job.path ? `/explorer?${new URLSearchParams({ search: `${job.path}/` })}` : undefined}
                        />
                        <TableCell className="text-muted-foreground hidden md:table-cell">{job.category}</TableCell>
                        <TableCell className="hidden text-right tabular-nums sm:table-cell">{bytes(job.total_bytes)}</TableCell>
                        <TableCell>
                          {job.status === "completed" ? (
                            <Status tone="success" compact>
                              Completed
                            </Status>
                          ) : job.fail_message ? (
                            <ErrorDetails
                              status={
                                <Status tone="destructive" compact>
                                  Failed
                                </Status>
                              }
                              title="Import failed"
                              name={job.name}
                              message={job.fail_message}
                              onRetry={() => retry.mutate(job.id)}
                            />
                          ) : (
                            <Status tone="destructive" compact>
                              Failed
                            </Status>
                          )}
                        </TableCell>
                        <TableCell
                          className="text-muted-foreground hidden text-right tabular-nums lg:table-cell"
                          title={job.download_seconds > 0 ? undefined : "Under a second, or not recorded for this import"}
                        >
                          {job.download_seconds > 0 ? duration(job.download_seconds) : "–"}
                        </TableCell>
                        <TableCell className="text-muted-foreground hidden sm:table-cell" title={`Finished ${stamp(job.created_at)}`}>
                          {ago(job.created_at)}
                        </TableCell>
                        <TableCell>
                          <RowActions
                            name={job.name}
                            actions={[
                              job.status === "failed" && {
                                label: "Retry",
                                icon: <RotateCcw />,
                                disabled: retry.isPending,
                                onSelect: () => retry.mutate(job.id),
                              },
                              { label: "Download NZB", icon: <Download />, href: `/api/v1/history/${job.id}/nzb` },
                              {
                                label: "Remove",
                                icon: <Trash2 />,
                                onSelect: () => removeHistory.mutate(job.id),
                                confirm: {
                                  title: "Remove from history?",
                                  message: "The imported files stay in place; only the history entry goes.",
                                  confirm: "Remove",
                                },
                              },
                            ]}
                          />
                        </TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </Panel>
              <Pager offset={offset} more={more} onChange={setOffset} />
            </>
          )}
        </>
      )}
    </>
  );
}
