import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { Link } from "react-router";
import { CircleAlert, Loader2, RefreshCw, TriangleAlert } from "lucide-react";
import { toast } from "sonner";
import { cn } from "@/lib/utils";
import { api, body, type Schemas } from "./api/client";
import { ago, day } from "./format";
import { ConfirmButton, Failure } from "./ui";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";

type View = Schemas["LibraryView"];
type Strategy = Schemas["ImportStrategy"];
export type PreviewRequest = Partial<Schemas["PreviewRequest"]>;

const libraryKey = ["library"];

const busy = (v?: View) =>
  !!v && (v.state === "running" || v.state === "waiting" || v.preview_state === "running" || v.pending_remaps > 0);

export function useLibrary() {
  return useQuery({
    queryKey: libraryKey,
    queryFn: () => body(api.GET("/api/v1/library")),
    refetchInterval: (q) => (busy(q.state.data) ? 2000 : 30000),
  });
}

function useAction(run: () => Promise<View>, done?: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: run,
    onSuccess: (view) => {
      client.setQueryData(libraryKey, view);
      void client.invalidateQueries({ queryKey: ["settings"] });
      if (done) toast.success(done);
    },
  });
}

const formName = (s?: Strategy | null) => (s === "strm" ? "STRM files" : "symlinks");

function count(n: number, one: string, many = `${one}s`) {
  return `${n.toLocaleString()} ${n === 1 ? one : many}`;
}

function Bar({ done, total }: { done: number; total: number }) {
  const pct = total > 0 ? Math.min(100, (done / total) * 100) : 0;
  return (
    <div className="bg-muted h-1.5 w-full overflow-hidden rounded-full" role="progressbar" aria-valuenow={Math.round(pct)}>
      <div className="bg-primary h-full transition-[width]" style={{ width: `${pct}%` }} />
    </div>
  );
}

/** What a running conversion is doing, in one line. */
function status(v: View) {
  const p = v.progress;
  if (v.state === "waiting") return `Waiting: ${v.reason ?? "retrying soon"}`;
  if (p.phase === "rescanning") return `Arr rescans: ${p.rescans_done} of ${p.rescans_total}`;
  if (p.phase === "walking" || p.total === 0) return `Reading ${p.folder ?? "library folders"}`;
  return `${p.done.toLocaleString()} of ${p.total.toLocaleString()} files`;
}

/** On every page while the library is converting, stopped half-way, or not in the saved form. */
export function LibraryBanner() {
  const v = useLibrary().data;
  if (!v) return null;
  const settings = (
    <Button asChild size="sm" variant="outline">
      <Link to="/settings/library">Open library settings</Link>
    </Button>
  );
  if (v.state === "running" || v.state === "waiting") {
    return (
      <Alert className="mb-6">
        {v.state === "waiting" ? <TriangleAlert className="text-warning-text" /> : <Loader2 className="animate-spin" />}
        <AlertTitle>Converting the library to {formName(v.target)}</AlertTitle>
        <AlertDescription>
          <p>{status(v)}. Both forms keep playing meanwhile.</p>
          {settings}
        </AlertDescription>
      </Alert>
    );
  }
  if (v.state === "cancelled") {
    return (
      <Alert className="mb-6">
        <TriangleAlert className="text-warning-text" />
        <AlertTitle>Library conversion stopped</AlertTitle>
        <AlertDescription>
          <p>
            {v.progress.phase === "rescanning"
              ? `Every file is converted, but ${count(v.progress.rescans_total - v.progress.rescans_done, "Arr rescan")} still ${v.progress.rescans_total - v.progress.rescans_done === 1 ? "has" : "have"} to run.`
              : "The library holds both symlinks and STRM files, which both play."}{" "}
            Resume it from the import settings.
          </p>
          {settings}
        </AlertDescription>
      </Alert>
    );
  }
  if (!v.form_current) {
    return (
      <Alert className="mb-6">
        <TriangleAlert className="text-warning-text" />
        <AlertTitle>Library not in the current form</AlertTitle>
        <AlertDescription>
          <p>Import settings changed since the library was last converted. Nothing has been converted yet.</p>
          {settings}
        </AlertDescription>
      </Alert>
    );
  }
  return null;
}

export function previewTitle(req: PreviewRequest) {
  if (req.strategy) return `Convert the library to ${formName(req.strategy)}`;
  if (req.new_key) return "Generate a new STRM key";
  if (req.public_url) return "Change the playback address";
  if (req.mount_dir) return "Move the mount";
  return "Convert the library to the saved settings";
}

function previewAction(req: PreviewRequest) {
  if (req.strategy) return `Switch to ${formName(req.strategy)}`;
  if (req.new_key) return "Generate new key";
  if (req.public_url) return "Change address";
  if (req.mount_dir) return "Move mount";
  return "Convert library";
}

/**
 * Previews `request` against the library folders, then saves it and converts the
 * library once confirmed. Closing it changes nothing.
 */
export function PreviewDialog({ request, onClose }: { request: PreviewRequest | null; onClose: () => void }) {
  const client = useQueryClient();
  const library = useLibrary();
  const [accepted, setAccepted] = useState(false);
  const start = useAction(
    () =>
      body(
        api.POST("/api/v1/library/preview", {
          body: { strategy: null, public_url: null, mount_dir: null, new_key: false, ...request },
        }),
      ),
  );
  const confirm = useAction(() => body(api.POST("/api/v1/library/confirm")), "Conversion started");
  const { mutate } = start;
  useEffect(() => {
    if (request) {
      setAccepted(false);
      mutate();
    }
  }, [request, mutate]);
  const close = () => {
    void api.DELETE("/api/v1/library/preview").then(() => client.invalidateQueries({ queryKey: libraryKey }));
    onClose();
  };
  const v = library.data;
  const p = v?.preview_state === "ready" && !start.isPending ? v.preview : null;
  const failing = p?.folders.filter((f) => !f.ok) ?? [];
  const blocked = !p || start.isError || (failing.length > 0 && !accepted);

  return (
    <Dialog open={!!request} onOpenChange={(open) => !open && close()}>
      <DialogContent className="sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle>{request ? previewTitle(request) : ""}</DialogTitle>
          <DialogDescription>
            Nothing changes until you confirm.
            {!request?.new_key && !request?.public_url && !request?.mount_dir && " Both forms keep playing during the conversion."}
          </DialogDescription>
        </DialogHeader>
        {start.isError ? (
          <Alert variant="destructive">
            <CircleAlert />
            <AlertDescription>{start.error.message}</AlertDescription>
          </Alert>
        ) : !p ? (
          <p className="text-muted-foreground flex items-center gap-2">
            <Loader2 className="size-4 animate-spin" /> Reading the library folders…
          </p>
        ) : (
          <div className="space-y-4">
            <ul className="space-y-1">
              {p.convert > 0 && <li>{count(p.convert, "file")} change form</li>}
              {p.rewrite > 0 && <li>{count(p.rewrite, "file")} rewritten in place</li>}
              {p.copy > 0 && <li>{count(p.copy, "subtitle")} copied as real files</li>}
              {p.strm_dir_rewrite > 0 && <li>{count(p.strm_dir_rewrite, "STRM file")} waiting for import rewritten</li>}
              {p.convert + p.rewrite + p.copy + p.strm_dir_rewrite === 0 && <li>Nothing in the library folders changes.</li>}
              {p.dangling > 0 && (
                <li className="text-muted-foreground">
                  {count(p.dangling, "file")} point at downloads that no longer exist; they are left alone
                </li>
              )}
              {p.arrs.map((a) => (
                <li key={a.name} className="text-muted-foreground">
                  {a.name} rescans {count(a.files, "file")}, keeping their quality, release details and monitoring
                </li>
              ))}
            </ul>
            {p.folders.length > 0 && (
              <div className="divide-y rounded-md border">
                {p.folders.map((f) => (
                  <div key={f.path} className="space-y-0.5 px-3 py-2">
                    <div className="flex items-center justify-between gap-2">
                      <span className="truncate font-mono text-xs">
                        {f.path}
                        {f.arr_path !== f.path && <span className="text-muted-foreground"> → {f.arr_path}</span>}
                      </span>
                      <span className={cn("shrink-0 text-xs", f.ok ? "text-muted-foreground" : "text-destructive")}>
                        {f.ok ? `${f.convert + f.rewrite} to change` : "Left out"}
                      </span>
                    </div>
                    {f.reason && <p className="text-destructive text-xs">{f.reason}</p>}
                    <p className="text-muted-foreground text-xs">
                      {f.arr_roots.length > 0 ? `Arr roots: ${f.arr_roots.join(", ")}` : "No Arr root folder is inside it."}
                    </p>
                  </div>
                ))}
              </div>
            )}
            {p.skipped_total > 0 && (
              <details className="text-xs">
                <summary className="cursor-pointer">{count(p.skipped_total, "file")} skipped</summary>
                <ul className="mt-1 max-h-40 space-y-0.5 overflow-auto font-mono">
                  {p.skipped.map((s) => (
                    <li key={s.path}>
                      {s.path} <span className="text-muted-foreground font-sans">({s.reason})</span>
                    </li>
                  ))}
                </ul>
              </details>
            )}
            {p.warnings.length > 0 && (
              <Alert>
                <TriangleAlert className="text-warning-text" />
                <AlertDescription>
                  <ul className="list-disc space-y-0.5 pl-4">
                    {p.warnings.map((w) => (
                      <li key={w}>{w}</li>
                    ))}
                  </ul>
                </AlertDescription>
              </Alert>
            )}
            {failing.length > 0 && (
              <label className="flex items-center gap-2">
                <Checkbox checked={accepted} onCheckedChange={(v) => setAccepted(v === true)} />
                Convert without the {failing.length === 1 ? "folder" : "folders"} left out
              </label>
            )}
          </div>
        )}
        {confirm.isError && <Failure error={confirm.error} />}
        <DialogFooter>
          <Button variant="outline" onClick={close}>
            Cancel
          </Button>
          <Button disabled={blocked || confirm.isPending} onClick={() => confirm.mutate(undefined, { onSuccess: onClose })}>
            {confirm.isPending && <Loader2 className="animate-spin" />}
            {request && previewAction(request)}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function Details({ v, open, onOpenChange }: { v: View; open: boolean; onOpenChange: (open: boolean) => void }) {
  const p = v.progress;
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle>Library conversion</DialogTitle>
          <DialogDescription>{p.folder ? `Now in ${p.folder}` : "Last run"}</DialogDescription>
        </DialogHeader>
        <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1">
          <dt className="text-muted-foreground">Converted</dt>
          <dd className="tabular-nums">{p.converted.toLocaleString()}</dd>
          <dt className="text-muted-foreground">Rewritten in place</dt>
          <dd className="tabular-nums">{p.rewritten.toLocaleString()}</dd>
          <dt className="text-muted-foreground">Subtitles copied</dt>
          <dd className="tabular-nums">{p.copied.toLocaleString()}</dd>
          <dt className="text-muted-foreground">Arr rescans</dt>
          <dd className="tabular-nums">
            {p.rescans_done} of {p.rescans_total}
          </dd>
          <dt className="text-muted-foreground">Waiting for a rescan</dt>
          <dd className="tabular-nums">{v.pending_remaps}</dd>
          <dt className="text-muted-foreground">Pointing at deleted downloads</dt>
          <dd className="tabular-nums">{p.dangling}</dd>
        </dl>
        {p.problems_total > 0 && (
          <div>
            <p className="mb-1 font-medium">{count(p.problems_total, "file")} not converted</p>
            <ul className="bg-muted/50 max-h-60 space-y-1 overflow-auto rounded-md border p-2 text-xs">
              {p.problems.map((x) => (
                <li key={x.path}>
                  <span className="font-mono">{x.path}</span>
                  <span className="text-muted-foreground block">{x.reason}</span>
                </li>
              ))}
            </ul>
          </div>
        )}
      </DialogContent>
    </Dialog>
  );
}

/** Conversion progress and the keys still accepted, below the strategy cards. */
export function LibraryPanel({ onPreview }: { onPreview: (req: PreviewRequest) => void }) {
  const v = useLibrary().data;
  const [details, setDetails] = useState(false);
  const cancel = useAction(() => body(api.POST("/api/v1/library/cancel")), "Stopping after the current file");
  const check = useAction(() => body(api.POST("/api/v1/library/check")), "Checking the library");
  const revoke = useAction(() => body(api.POST("/api/v1/library/keys/revoke")), "Previous keys revoked");
  const stop = useAction(() => body(api.POST("/api/v1/library/legacy/stop")), "Earlier links no longer play");
  if (!v) return null;
  const failed = [cancel, check, revoke, stop].find((a) => a.isError)?.error;
  const active = v.state === "running" || v.state === "waiting";
  const p = v.progress;
  const key = v.previous_keys.at(-1);
  return (
    <div className="space-y-3">
      {!v.form_current && !active && v.state !== "cancelled" && (
        <Alert>
          <TriangleAlert className="text-warning-text" />
          <AlertDescription>
            <span>Import settings changed since the library was last converted. Nothing has been converted yet.</span>
            <Button type="button" size="sm" variant="outline" className="mt-2" onClick={() => onPreview({})}>
              Preview the conversion
            </Button>
          </AlertDescription>
        </Alert>
      )}
      {(active || v.state === "cancelled") && (
        <div className="space-y-2 rounded-lg border p-3">
          <span className="font-medium">
            {v.state === "cancelled" ? "Stopped converting to" : "Converting to"} {formName(v.target)}
          </span>
          <Bar done={p.phase === "rescanning" ? p.rescans_done : p.done} total={p.phase === "rescanning" ? p.rescans_total : p.total} />
          <p className="text-muted-foreground text-xs">{status(v)}</p>
          <div className="flex flex-wrap gap-2">
            {active ? (
              <Button type="button" size="sm" variant="outline" disabled={cancel.isPending} onClick={() => cancel.mutate()}>
                Cancel
              </Button>
            ) : (
              <Button type="button" size="sm" variant="outline" disabled={check.isPending} onClick={() => check.mutate()}>
                Resume
              </Button>
            )}
            <Button type="button" size="sm" variant="ghost" onClick={() => setDetails(true)}>
              Details
            </Button>
          </div>
        </div>
      )}
      {!active && v.state !== "cancelled" && (
        <div className="flex flex-wrap items-center gap-2 text-xs">
          <Button type="button" size="sm" variant="outline" disabled={check.isPending} onClick={() => check.mutate()}>
            <RefreshCw /> Check library
          </Button>
          {(p.problems_total > 0 || v.pending_remaps > 0) && (
            <Button type="button" size="sm" variant="ghost" onClick={() => setDetails(true)}>
              {p.problems_total > 0 && count(p.problems_total, "file") + " not converted"}
              {p.problems_total > 0 && v.pending_remaps > 0 && " · "}
              {v.pending_remaps > 0 && `${v.pending_remaps} waiting for an Arr rescan`}
            </Button>
          )}
          <span className="text-muted-foreground">Converts anything not in the current form and retries rescans.</span>
        </div>
      )}
      {key && (
        <div className="flex flex-wrap items-center justify-between gap-2 rounded-lg border px-3 py-2 text-xs">
          <span>
            The previous STRM key still plays:{" "}
            {key.last_used_ms ? `last used ${ago(key.last_used_ms)}` : "not used since"}
            {key.retires_ms ? `; retires ${ago(key.retires_ms)} if unused` : "; retires 7 days after the rewrite"}.
          </span>
          <ConfirmButton
            variant="outline"
            label="Revoke now"
            title="Revoke the previous STRM key?"
            message="STRM files still using it stop playing right away."
            confirm="Revoke"
            onConfirm={() => revoke.mutate()}
          />
        </div>
      )}
      {/* Nothing to act on until a 1.x link is seen or rewritten; it retires on its own. */}
      {v.legacy && (v.legacy.uses > 0 || v.legacy.rewritten > 0) && (
        <div className="flex flex-wrap items-center justify-between gap-2 rounded-lg border px-3 py-2 text-xs">
          <span>
            STRM links made by infinidysk 1.x still play: {count(v.legacy.uses, "use")}
            {v.legacy.last_used_ms ? `, last ${ago(v.legacy.last_used_ms)}` : ""}; {v.legacy.rewritten} rewritten in the
            library.{" "}
            {v.legacy.retires_ms
              ? `They stop on ${day(v.legacy.retires_ms)} unless used again.`
              : "They stop 7 days after a full sweep finds none left."}
          </span>
          <ConfirmButton
            variant="outline"
            label="Stop accepting now"
            title="Stop accepting old STRM links?"
            message="STRM files made by infinidysk 1.x that were not rewritten stop playing right away."
            confirm="Stop accepting"
            onConfirm={() => stop.mutate()}
          />
        </div>
      )}
      {failed && <Failure error={failed} />}
      <Details v={v} open={details} onOpenChange={setDetails} />
    </div>
  );
}
