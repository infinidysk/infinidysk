import { type ReactNode, useState, useSyncExternalStore } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { CircleAlert, DatabaseZap, Loader2, PlugZap, TriangleAlert } from "lucide-react";
import { toast } from "sonner";
import { api, body, type Schemas } from "./api/client";
import { ago, stamp } from "./format";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { ConfirmButton, Failure, Field, SettingsPanel, SettingsSection, Status } from "./ui";
import { notAPassword } from "./settings";

type Migration = Schemas["MigrationView"];

const KEY = "infinidysk.switch";
const listeners = new Set<() => void>();

function read(): number | null {
  const value = Number(sessionStorage.getItem(KEY) ?? Number.NaN);
  return Number.isFinite(value) ? value : null;
}

function notify() {
  for (const listener of listeners) listener();
}

/** A switch to PostgreSQL in flight: the `started_ms` of the process that is restarting. Kept across reloads. */
export const switching = {
  get: read,
  begin(startedMs: number) {
    sessionStorage.setItem(KEY, String(startedMs));
    notify();
  },
  end() {
    sessionStorage.removeItem(KEY);
    notify();
  },
};

function subscribe(listener: () => void) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function useSwitching(): number | null {
  return useSyncExternalStore(subscribe, read);
}

const stages: Record<Schemas["ProgressStage"], string> = {
  copying: "Copying",
  syncing: "Applying changes",
  verifying: "Checking",
  switching: "Switching",
};

const HINT =
  "postgres://user:password@host:5432/database. Use a new, empty database. Add ?sslmode=require for a server on another machine. Percent-encode special characters in the password.";

/** Settings → Database: which database runs, and moving from SQLite to PostgreSQL. */
export function DatabasePanel({ title, description }: { title: string; description: string }) {
  const view = useQuery({
    queryKey: ["database"],
    queryFn: () => body(api.GET("/api/v1/database")),
    refetchInterval: (q) => (q.state.data?.migration ? 1000 : false),
  });
  const data = view.data;
  return (
    <SettingsSection title={title} description={description}>
      {!data ? (
        view.isError ? (
          <Failure error={view.error} />
        ) : (
          <p className="text-muted-foreground flex items-center gap-2">
            <Loader2 className="size-4 animate-spin" />
            Loading…
          </p>
        )
      ) : (
        <SettingsPanel>
          <div className="space-y-1">
            <p className="flex flex-wrap items-center gap-x-2">
              <Status tone="success">{data.backend === "postgres" ? "PostgreSQL" : "SQLite"}</Status>
              {data.backend === "postgres" && data.target && <span className="text-muted-foreground font-mono break-all">{data.target}</span>}
            </p>
            {data.env_managed ? (
              <p className="text-muted-foreground text-xs">
                Set by <code>INFINIDYSK_DATABASE_URL</code>. Remove it from the container's environment to manage the database here.
              </p>
            ) : data.backend === "sqlite" && !data.migration ? (
              <p className="text-muted-foreground text-xs">
                Kept in <code className="break-all">{data.sqlite_path}</code>. Copy it to PostgreSQL to run on a database server instead.
              </p>
            ) : null}
          </div>
          {!data.env_managed && data.backend === "sqlite" && (data.migration ? <Move migration={data.migration} target={data.target} /> : <Start folder={data.sqlite_path?.replace(/\/[^/]*$/, "") || "/"} />)}
        </SettingsPanel>
      )}
    </SettingsSection>
  );
}

/** No move yet: a URL, a read-only test, and the start of the copy. `folder` holds the SQLite file. */
function Start({ folder }: { folder: string }) {
  const client = useQueryClient();
  const [url, setUrl] = useState("");
  const [confirming, setConfirming] = useState(false);
  const check = useMutation({ mutationFn: (target: string) => body(api.POST("/api/v1/database/check", { body: { url: target } })) });
  const start = useMutation({
    mutationFn: (target: string) => body(api.POST("/api/v1/database/migration", { body: { url: target } })),
    onSuccess: () => void client.invalidateQueries({ queryKey: ["database"] }),
  });
  const tested = check.data && check.variables === url ? check.data : undefined;
  const failed = check.variables === url ? (check.error?.message ?? tested?.error) : undefined;
  return (
    <div className="space-y-4">
      <Field id="database-url" label="PostgreSQL URL" hint={HINT}>
        <Input
          id="database-url"
          type="password"
          className="font-mono"
          {...notAPassword}
          placeholder="postgres://user:password@host:5432/database"
          value={url}
          onChange={(e) => setUrl(e.target.value.trim())}
        />
      </Field>
      <div className="flex flex-wrap items-center gap-3">
        <Button type="button" variant="outline" disabled={check.isPending || !url} onClick={() => check.mutate(url)}>
          {check.isPending ? <Loader2 className="animate-spin" /> : <PlugZap />}
          Test connection
        </Button>
        {!check.isPending &&
          (failed ? (
            <p className="text-destructive flex items-start gap-2">
              <CircleAlert className="mt-0.5 size-4 shrink-0" />
              {failed}
            </p>
          ) : (
            tested && (
              <Status tone="success">
                Ready<span className="text-muted-foreground font-mono">{tested.version}</span>
              </Status>
            )
          ))}
      </div>
      {tested?.warnings.map((warning) => (
        <p key={warning} className="text-warning-text flex items-start gap-2 text-xs">
          <TriangleAlert className="mt-0.5 size-4 shrink-0" />
          {warning}
        </p>
      ))}
      <div className="flex flex-col gap-2 border-t pt-4 sm:flex-row sm:items-center">
        <Button type="button" disabled={!url || start.isPending} onClick={() => setConfirming(true)}>
          {start.isPending ? <Loader2 className="animate-spin" /> : <DatabaseZap />}
          Copy to PostgreSQL
        </Button>
        {start.isError && start.variables === url && <Failure error={start.error} />}
      </div>
      <Confirm
        open={confirming}
        onOpenChange={setConfirming}
        title="Copy to PostgreSQL?"
        confirm="Start copying"
        onConfirm={() => start.mutate(url)}
      >
        infinidysk copies the library, queue, history and settings to PostgreSQL, then keeps the copy in sync while you keep using
        it. Nothing switches until you choose Switch to PostgreSQL. Back up <code className="break-all">{folder}</code> before switching.
      </Confirm>
    </div>
  );
}

/** A move in progress: the copy, the sync, and the switch. */
function Move({ migration, target }: { migration: Migration; target?: string | null }) {
  const client = useQueryClient();
  const [confirming, setConfirming] = useState(false);
  const refresh = () => void client.invalidateQueries({ queryKey: ["database"] });
  const cancel = useMutation({
    mutationFn: () => body(api.DELETE("/api/v1/database/migration")),
    onSuccess: (done) => done.warnings.forEach((warning) => toast.warning(warning, { duration: Infinity })),
    onSettled: refresh,
  });
  const cutover = useMutation({
    mutationFn: () => body(api.POST("/api/v1/database/cutover")),
    onSuccess: (started) => switching.begin(started.started_ms),
    onError: refresh,
  });
  const copying = migration.state === "copying";
  const percent = migration.total > 0 ? Math.min(100, (migration.done / migration.total) * 100) : undefined;
  const label = migration.stage ? stages[migration.stage] : "Preparing";
  return (
    <div className="space-y-4">
      {target && (
        <p className="text-muted-foreground text-xs">
          To <span className="font-mono break-all">{target}</span>
        </p>
      )}
      {copying ? (
        <div className="space-y-1.5">
          <div className="flex items-center justify-between gap-2 text-xs">
            <span className="flex min-w-0 items-center gap-2">
              <Loader2 className="text-primary size-3.5 shrink-0 animate-spin" />
              <span className="truncate">
                {label}
                {migration.table && <span className="text-muted-foreground font-mono"> · {migration.table}</span>}
              </span>
            </span>
            <span className="text-muted-foreground shrink-0 tabular-nums">
              {migration.total ? `${migration.done.toLocaleString()} / ${migration.total.toLocaleString()} · ${percent!.toFixed(0)}%` : ""}
            </span>
          </div>
          <div
            className="bg-muted h-1.5 overflow-hidden rounded-full"
            role="progressbar"
            aria-label={label}
            aria-valuemin={0}
            aria-valuemax={100}
            aria-valuenow={percent}
          >
            <div
              className={percent === undefined ? "bg-primary/40 h-full w-1/3 motion-safe:animate-pulse" : "bg-primary h-full transition-[width]"}
              style={percent === undefined ? undefined : { width: `${percent}%` }}
            />
          </div>
        </div>
      ) : migration.state === "syncing" ? (
        <dl className="space-y-2 text-sm">
          <div className="flex flex-wrap justify-between gap-x-4 gap-y-1">
            <dt className="text-muted-foreground">Copy</dt>
            <dd>
              <Status tone="success">In sync</Status>
            </dd>
          </div>
          <div className="flex flex-wrap justify-between gap-x-4 gap-y-1">
            <dt className="text-muted-foreground">Changes not yet copied</dt>
            <dd className="tabular-nums">{migration.pending_changes == null ? "Checking…" : migration.pending_changes.toLocaleString()}</dd>
          </div>
          <div className="flex flex-wrap justify-between gap-x-4 gap-y-1">
            <dt className="text-muted-foreground">Last synced</dt>
            <dd title={migration.synced_at_ms == null ? undefined : stamp(migration.synced_at_ms)}>
              {migration.synced_at_ms == null ? "Not yet" : ago(migration.synced_at_ms)}
            </dd>
          </div>
        </dl>
      ) : (
        <p className="flex items-center gap-2 text-sm">
          <Loader2 className="text-primary size-4 animate-spin" />
          Switching to PostgreSQL…
        </p>
      )}
      {migration.error && (
        <div className="space-y-1">
          <Status tone="destructive">Problem</Status>
          <p className="text-destructive text-xs break-words">{migration.error}</p>
        </div>
      )}
      <div className="flex flex-col gap-2 border-t pt-4 sm:flex-row sm:items-center">
        {migration.state === "syncing" && (
          <Button type="button" disabled={cutover.isPending || cancel.isPending} onClick={() => setConfirming(true)}>
            {cutover.isPending ? <Loader2 className="animate-spin" /> : <DatabaseZap />}
            Switch to PostgreSQL
          </Button>
        )}
        {migration.state !== "cutover" && (
          <ConfirmButton
            label={cancel.isPending ? "Cancelling…" : "Cancel"}
            variant="outline"
            disabled={cancel.isPending || cutover.isPending}
            title="Cancel the move?"
            message="infinidysk stops copying and empties the PostgreSQL database again. It keeps running on SQLite."
            confirm="Cancel the move"
            onConfirm={() => cancel.mutate()}
          />
        )}
        {cancel.isError && <Failure error={cancel.error} />}
        {cutover.isError && <Failure error={cutover.error} />}
      </div>
      <Confirm
        open={confirming}
        onOpenChange={setConfirming}
        title="Switch to PostgreSQL?"
        confirm="Switch"
        onConfirm={() => cutover.mutate()}
      >
        infinidysk restarts. Playback, WebDAV and imports pause until the switch finishes. Your SQLite file is kept as{" "}
        <code>infinidysk.sqlite.pre-postgres</code>.
      </Confirm>
    </div>
  );
}

/** Asks before a non-destructive step. */
function Confirm({
  open,
  onOpenChange,
  title,
  confirm,
  onConfirm,
  children,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  confirm: string;
  onConfirm: () => void;
  children: ReactNode;
}) {
  return (
    <AlertDialog open={open} onOpenChange={onOpenChange}>
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>{title}</AlertDialogTitle>
          <AlertDialogDescription className="break-words">{children}</AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter>
          <AlertDialogCancel>Not now</AlertDialogCancel>
          <AlertDialogAction onClick={onConfirm}>{confirm}</AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}
