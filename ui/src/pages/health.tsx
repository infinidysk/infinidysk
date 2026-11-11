import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { type ReactNode, useState } from "react";
import { Link } from "react-router";
import { Search, Stethoscope, TriangleAlert, Wrench } from "lucide-react";
import { toast } from "sonner";
import { api, body, type Schemas } from "../api/client";
import { ago, stamp } from "../format";
import { useSettings } from "../settings";
import { Par2Scan } from "../par2";
import { cn } from "@/lib/utils";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import {
  CheckStatus,
  checkResults,
  Failure,
  healthStates,
  IconButton,
  InfoTip,
  Loading,
  PAGE,
  PageHeader,
  Pager,
  Panel,
  ReleaseName,
  Status,
  TextConfirmDialog,
} from "../ui";

/** `/content/<category>/…/<name>` → name and category. */
function parts(path: string) {
  return { name: path.slice(path.lastIndexOf("/") + 1), category: path.split("/")[2] };
}

/** The file's name, linked to it in the explorer when `id` is set, over its category. */
function Item({ name, category, id, children }: { name: string; category?: string | null; id?: string; children?: ReactNode }) {
  return (
    <TableCell className="w-full max-w-0">
      {id ? (
        <Link
          to={`/explorer?${new URLSearchParams({ item: id, search: name })}`}
          className="block truncate hover:underline"
          title={name}
        >
          <ReleaseName name={name} />
        </Link>
      ) : (
        <div className="truncate" title={name}>
          <ReleaseName name={name} />
        </div>
      )}
      {category && <div className="text-muted-foreground truncate text-xs">{category}</div>}
      {children}
    </TableCell>
  );
}

/** The only row of a three-column table with nothing in it yet, saying when rows will appear. */
function EmptyRow({ title, children }: { title: string; children: ReactNode }) {
  return (
    <TableRow className="hover:bg-transparent">
      <TableCell colSpan={3} className="py-6 text-center whitespace-normal">
        <p className="font-medium">{title}</p>
        <p className="text-muted-foreground mt-1">{children}</p>
      </TableCell>
    </TableRow>
  );
}

const stateHints: Record<Schemas["ItemHealth"], string> = {
  unchecked: "Imported, but not checked against your providers yet.",
  healthy: "The last check found every article it looked for.",
  degraded:
    "A few articles are missing, too few to stop playback: the file stays and plays, with a brief glitch where a gap is. It is not replaced automatically.",
  suspect: "A check found missing articles. A second check 15 minutes later confirms them before any repair starts.",
  repairing: "Missing articles are confirmed, and a replacement is being requested from the Arr that imported the file.",
  hidden: "An Arr is fetching a replacement. The file is hidden from the library until it arrives.",
  action_needed: "Automatic repair could not fix these files. They are listed under Needs attention.",
  ignored: "Not a media file, so it is not checked.",
};

function Summary() {
  const summary = useQuery({
    queryKey: ["health", "summary"],
    queryFn: () => body(api.GET("/api/v1/health/summary")),
  });
  if (!summary.data?.length) return null;
  return (
    <div className="mb-6 flex flex-wrap gap-x-6 gap-y-2">
      {summary.data.map(({ state, files }) => (
        <Status key={state} tone={healthStates[state][1]}>
          {healthStates[state][0]} <span className="text-muted-foreground tabular-nums">{files.toLocaleString()}</span>
          <InfoTip label={`About ${healthStates[state][0].toLowerCase()}`}>{stateHints[state]}</InfoTip>
        </Status>
      ))}
    </div>
  );
}

/** Daily Arr reconciliations held for deleting too much at once. */
function Reconcile() {
  const client = useQueryClient();
  const held = useQuery({
    queryKey: ["arr-reconcile"],
    queryFn: () => body(api.GET("/api/v1/arr/reconcile")),
  });
  const act = useMutation({
    mutationFn: ({ instance, at, confirm }: { instance: string; at: number; confirm: boolean }) => {
      const path = { instance, at };
      return confirm
        ? body(api.POST("/api/v1/arr/reconcile/{instance}/{at}/confirm", { params: { path } }))
        : body(api.DELETE("/api/v1/arr/reconcile/{instance}/{at}", { params: { path } }));
    },
    onSuccess: (result, { confirm }) => {
      const n = result?.deleted ?? 0;
      if (confirm) toast.success(`Deleted ${n} ${n === 1 ? "file" : "files"}`);
      void client.invalidateQueries({ queryKey: ["arr-reconcile"] });
      void client.invalidateQueries({ queryKey: ["files"] });
    },
  });
  const [deleting, setDeleting] = useState<Schemas["HeldReconcile"]>();
  if (!held.data?.length) return null;
  return (
    <section className="mb-6">
      <h2 className="mb-1 font-medium">Library cleanup on hold</h2>
      <p className="text-muted-foreground mb-3">
        These Arrs no longer list many files they imported from here. That is more than 5% of their files at once, so
        nothing was deleted. Confirm to delete the files the Arr still lacks, or keep them all.
      </p>
      {act.isError && <Failure error={act.error} className="mb-3" />}
      <div className="space-y-3">
        {held.data.map((run) => (
          <Panel key={`${run.instance}-${run.at_ms}`}>
            <div className="flex flex-wrap items-center justify-between gap-2 border-b px-4 py-3">
              <div>
                <div className="font-medium">
                  {run.name} lacks {run.files.length.toLocaleString()} of {run.owned.toLocaleString()}{" "}
                  {run.owned === 1 ? "file" : "files"}
                </div>
                <div className="text-muted-foreground text-xs" title={stamp(run.at_ms)}>
                  Found {ago(run.at_ms)}
                </div>
              </div>
              <div className="flex gap-2">
                <Button
                  variant="outline"
                  size="sm"
                  disabled={act.isPending}
                  onClick={() => act.mutate({ instance: run.instance, at: run.at_ms, confirm: false })}
                >
                  Keep all
                </Button>
                <Button variant="outline" size="sm" disabled={act.isPending} onClick={() => setDeleting(run)}>
                  Delete files
                </Button>
              </div>
            </div>
            <ul className="max-h-48 overflow-y-auto px-4 py-2 text-xs">
              {run.files.map((f) => (
                <li key={`${f.item}-${f.path}`} className="text-muted-foreground truncate py-0.5" title={f.path}>
                  {f.path}
                </li>
              ))}
            </ul>
          </Panel>
        ))}
      </div>
      <TextConfirmDialog
        open={!!deleting}
        onOpenChange={(open) => !open && setDeleting(undefined)}
        title={`Delete ${deleting?.files.length.toLocaleString()} ${deleting?.files.length === 1 ? "file" : "files"}?`}
        message={`${deleting?.name} no longer lists these files. They leave the library and any library links to them break. This cannot be undone.`}
        expected={String(deleting?.files.length ?? "")}
        confirm="Delete files"
        onConfirm={() => deleting && act.mutate({ instance: deleting.instance, at: deleting.at_ms, confirm: true })}
      />
    </section>
  );
}

function Attention() {
  const client = useQueryClient();
  const query = { state: "action_needed" as const, sort: "added" as const, order: "desc" as const, limit: PAGE };
  const list = useQuery({
    queryKey: ["files", query],
    queryFn: () => body(api.GET("/api/v1/files", { params: { query } })),
  });
  const act = useMutation({
    mutationFn: ({ id, action }: { id: string; action: "check" | "repair" }) =>
      action === "check"
        ? body(api.POST("/api/v1/files/{id}/check", { params: { path: { id } } }))
        : body(api.POST("/api/v1/files/{id}/repair", { params: { path: { id } } })),
    onSuccess: (_, { action }) => {
      toast.success(action === "check" ? "Health check started" : "Repair started");
      void client.invalidateQueries({ queryKey: ["files"] });
      void client.invalidateQueries({ queryKey: ["health", "summary"] });
    },
  });
  if (!list.data?.entries.length) return null;
  return (
    <section className="mb-6">
      <h2 className="mb-1 font-medium">Needs attention</h2>
      <p className="text-muted-foreground mb-3">
        Automatic repair gave up on these files. They stay in the library until you re-check, replace or remove them.
      </p>
      {act.isError && <Failure error={act.error} className="mb-3" />}
      <Panel>
        <Table>
          <TableBody>
            {list.data.entries.map((entry) => (
              <TableRow key={entry.path}>
                <Item name={entry.name} category={entry.category} id={entry.item_id ?? undefined}>
                  {entry.health?.message && <div className="text-muted-foreground truncate text-xs">{entry.health.message}</div>}
                </Item>
                <TableCell className="w-20">
                  {entry.item_id && (
                    <div className="flex justify-end gap-2">
                      <IconButton
                        label="Re-check health"
                        disabled={act.isPending}
                        onClick={() => act.mutate({ id: entry.item_id!, action: "check" })}
                      >
                        <Stethoscope />
                      </IconButton>
                      <IconButton
                        label="Try another replacement"
                        disabled={act.isPending}
                        onClick={() => act.mutate({ id: entry.item_id!, action: "repair" })}
                      >
                        <Wrench />
                      </IconButton>
                    </div>
                  )}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </Panel>
    </section>
  );
}

const REJECTED_ROWS = 10;

/** Releases health asked an Arr to replace; matching NZBs fail to import until each expires. */
function Rejected() {
  const [limit, setLimit] = useState(REJECTED_ROWS);
  const rejected = useQuery({
    queryKey: ["health", "rejected"],
    queryFn: () => body(api.GET("/api/v1/health/rejected")),
  });
  if (!rejected.data?.length) return null;
  return (
    <section className="mb-6">
      <h2 className="mb-1 font-medium">Rejected releases</h2>
      <p className="text-muted-foreground mb-3">
        Health asked an Arr to replace these. An NZB with the same name or the same articles fails to import until the
        block expires, so the Arr picks a different release.
      </p>
      <Panel>
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Release</TableHead>
              <TableHead className="hidden w-32 sm:table-cell">Rejected</TableHead>
              <TableHead className="w-32">Expires</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {rejected.data.slice(0, limit).map((r) => (
              <TableRow key={`${r.name}-${r.rejected_at}`}>
                <Item name={r.name} />
                <TableCell className="text-muted-foreground hidden sm:table-cell" title={stamp(r.rejected_at)}>
                  {ago(r.rejected_at)}
                </TableCell>
                <TableCell className="text-muted-foreground" title={stamp(r.expires_at)}>
                  {ago(r.expires_at)}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </Panel>
      {rejected.data.length > REJECTED_ROWS && (
        <div className="text-muted-foreground mt-3 flex items-center justify-end gap-2 text-xs">
          <span className="tabular-nums">
            {Math.min(limit, rejected.data.length)} of {rejected.data.length}
          </span>
          {limit > REJECTED_ROWS && (
            <Button variant="ghost" size="sm" onClick={() => setLimit(REJECTED_ROWS)}>
              Show fewer
            </Button>
          )}
          {limit < rejected.data.length && (
            <Button variant="outline" size="sm" onClick={() => setLimit((n) => n + REJECTED_ROWS)}>
              Show more
            </Button>
          )}
        </div>
      )}
    </section>
  );
}

/** Routine checks are off: only rechecks, playback reports and repairs run. */
function Disabled() {
  const settings = useSettings();
  const view = settings.data;
  if (!view || view.settings.health.enabled) return null;
  const locked = view.locked.some((l) => l === "health" || l === "health.enabled");
  return (
    <Alert className="mb-6">
      <TriangleAlert className="text-warning-text" />
      <AlertTitle>Routine health checks are off</AlertTitle>
      <AlertDescription>
        Files are not checked on a schedule. New files stay unchecked and are not analyzed, unless the sample size is
        100%: then each import is a file's first check. Re-checks you start and problems found during playback are
        still handled.{" "}
        {locked && (
          <>
            The <code className="font-mono text-xs">INFINIDYSK__HEALTH__ENABLED</code> environment variable turns them off;
            remove it to turn them on.{" "}
          </>
        )}
        <Link to="/settings/health" className="text-foreground relative underline underline-offset-4 after:absolute after:-inset-y-1 after:inset-x-0">
          {locked ? "Open health settings" : "Turn them on in Settings"}
        </Link>
      </AlertDescription>
    </Alert>
  );
}

function Schedule() {
  const schedule = useQuery({
    queryKey: ["health", "schedule"],
    queryFn: () => body(api.GET("/api/v1/health/schedule")),
    refetchInterval: 60_000,
  });
  return (
    <section className="mb-6">
      <h2 className="mb-3 font-medium">Schedule</h2>
      {schedule.isPending ? (
        <Loading />
      ) : schedule.isError ? (
        <Failure error={schedule.error} />
      ) : (
        <Panel>
          <Table>
            <TableHeader className={cn(schedule.data.length === 0 && "opacity-50")}>
              <TableRow>
                <TableHead>Item</TableHead>
                <TableHead className="w-32">State</TableHead>
                <TableHead className="hidden w-32 sm:table-cell">Due</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {schedule.data.length === 0 && (
                <EmptyRow title="Nothing scheduled">Files due for a check or a repair show up here.</EmptyRow>
              )}
              {schedule.data.map((s) => (
                <TableRow key={s.item_id}>
                  <Item name={s.name} category={s.category} id={s.item_id} />
                  <TableCell>
                    <Status tone={healthStates[s.state][1]}>{healthStates[s.state][0]}</Status>
                    <div className="text-muted-foreground mt-0.5 text-xs sm:hidden">
                      {s.due && s.due > Date.now() ? ago(s.due) : "Now"}
                    </div>
                  </TableCell>
                  <TableCell className="text-muted-foreground hidden sm:table-cell" title={s.due ? stamp(s.due) : undefined}>
                    {s.due && s.due > Date.now() ? ago(s.due) : "Now"}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </Panel>
      )}
    </section>
  );
}

export function Health() {
  const [offset, setOffset] = useState(0);
  const [search, setSearch] = useState("");
  const [result, setResult] = useState<"any" | Schemas["CheckResult"]>("any");
  const term = search.trim();
  const filtered = term !== "" || result !== "any";
  const enabled = useSettings().data?.settings.health.enabled ?? true;
  const query = { limit: PAGE + 1, offset, search: term || undefined, result: result === "any" ? undefined : result };
  const health = useQuery({
    queryKey: ["health", query],
    queryFn: () => body(api.GET("/api/v1/health", { params: { query } })),
    placeholderData: keepPreviousData,
  });

  return (
    <>
      <PageHeader title="Health" description="Article checks against your providers from the last 30 days" />
      <Disabled />
      <Summary />
      <Par2Scan />
      <Reconcile />
      <Attention />
      <Schedule />
      <Rejected />
      <h2 className="mb-3 font-medium">History</h2>
      <div className="mb-3 flex flex-wrap items-center gap-2">
        <div className="relative min-w-40 flex-1 sm:max-w-80">
          <Search className="text-muted-foreground pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2" aria-hidden />
          <Input
            className="pl-8"
            placeholder="Search file names"
            aria-label="Search file names"
            value={search}
            onChange={(e) => {
              setSearch(e.target.value);
              setOffset(0);
            }}
          />
        </div>
        <Select
          value={result}
          onValueChange={(v) => {
            setResult(v as typeof result);
            setOffset(0);
          }}
        >
          <SelectTrigger className="w-44" aria-label="Result">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="any">Any result</SelectItem>
            {Object.entries(checkResults).map(([value, [label]]) => (
              <SelectItem key={value} value={value}>
                {label}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      </div>
      {health.isPending ? (
        <Loading />
      ) : health.isError ? (
        <Failure error={health.error} />
      ) : (
        <>
          <Panel>
            <Table>
              <TableHeader className={cn(health.data.length === 0 && "opacity-50")}>
                <TableRow>
                  <TableHead>Item</TableHead>
                  <TableHead className="w-48">Result</TableHead>
                  <TableHead className="hidden w-32 sm:table-cell">Checked</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {health.data.length === 0 &&
                  (filtered ? (
                    <EmptyRow title="No results found">
                      {term ? <>No checks match “{term}” with this result.</> : "No checks have this result."}
                    </EmptyRow>
                  ) : (
                    <EmptyRow title="No health checks yet">
                      {enabled
                        ? "Imported files are checked against your providers over time. Results from the last 30 days show up here."
                        : "Routine checks are off, so only re-checks you start and problems found during playback show up here."}
                    </EmptyRow>
                  ))}
                {health.data.slice(0, PAGE).map((check) => (
                  <TableRow key={`${check.item_id}-${check.created_at}`}>
                    <Item {...parts(check.path)} id={check.in_library ? check.item_id : undefined}>
                      {check.message && <div className="text-muted-foreground truncate text-xs">{check.message}</div>}
                    </Item>
                    <TableCell>
                      <CheckStatus check={check} />
                      <div className="text-muted-foreground mt-0.5 text-xs sm:hidden">{ago(check.created_at)}</div>
                    </TableCell>
                    <TableCell className="text-muted-foreground hidden sm:table-cell" title={stamp(check.created_at)}>
                      {ago(check.created_at)}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </Panel>
          <Pager offset={offset} more={health.data.length > PAGE} onChange={setOffset} />
        </>
      )}
    </>
  );
}
