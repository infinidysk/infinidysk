import { useEffect, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { CircleAlert, Download, Loader2, NotebookPen, Pin, PinOff, RotateCcw, ShieldCheck, Trash2, Upload } from "lucide-react";
import { api, body, type Schemas } from "./api/client";
import { ago, bytes, stamp } from "./format";
import { JobCard, useMaintenance } from "./maintenance";
import { type Settings as Doc, waitForRestart } from "./settings";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { Textarea } from "@/components/ui/textarea";
import {
  ConfirmButton,
  Empty,
  Failure,
  Field,
  Panel,
  RowActions,
  SettingsPanel,
  SettingsSection,
  Status,
  TextConfirmDialog,
  type Tone,
  useDiscardGuard,
} from "./ui";

type Backup = Schemas["BackupView"];
type Live = Schemas["LiveMaintenance"];

const kinds: Record<Schemas["BackupKind"], string> = { scheduled: "Scheduled", manual: "Manual", uploaded: "Uploaded", added: "Added" };

const NOTE_MAX = 500;

const UPLOAD_HINT =
  "A reverse proxy may limit upload size. You can also copy the file into the backup folder; it then shows up in this list.";

/** A restore or undo the browser is waiting on, then the attempt it checks `last_restore` against. */
type Wait = { action: "restore" | "undo"; attempt: string; live?: Live | null };

const fileUrl = (name: string) => `/api/v1/backups/${encodeURIComponent(name)}/file`;

function useBackups(fast: boolean) {
  return useQuery({
    queryKey: ["backups"],
    queryFn: () => body(api.GET("/api/v1/backups")),
    refetchInterval: fast ? 2000 : 30_000,
  });
}

function check(b: Backup): [string, Tone, string | undefined] {
  if (b.incomplete) return ["Incomplete", "warning", "The file is shorter than its header says."];
  if (b.newer) return ["Newer version", "warning", "A newer infinidysk wrote it; this version cannot restore it."];
  if (b.verify_error) return ["Check failed", "destructive", b.verify_error];
  if (b.verified_ms != null) return [`Checked ${ago(b.verified_ms)}`, "success", stamp(b.verified_ms)];
  return ["Not checked", "muted", undefined];
}

/** Settings → Backups: the backup job, where copies go, the backup list, restore and undo. */
export function BackupsPanel({
  title,
  description,
  value: maintenance,
  locked,
  onChange,
}: {
  title: string;
  description: string;
  value: Doc["maintenance"];
  locked: (path: string) => boolean;
  onChange: (value: Doc["maintenance"]) => void;
}) {
  const value = maintenance.backup;
  const jobsView = useMaintenance();
  const jobs = jobsView.data;
  const running = jobs?.running ?? null;
  const job = jobs?.jobs.find((j) => j.job === "backup-database");
  const client = useQueryClient();
  const [wait, setWait] = useState<Wait | null>(null);
  const [applied, setApplied] = useState<Wait | null>(null);
  const [failure, setFailure] = useState<Error | null>(null);
  const view = useBackups(!!running || !!wait);
  const v = view.data;
  const busy = !!running || !!wait;

  const done = () => void client.invalidateQueries({ queryKey: ["backups"] });
  // A job that starts or ends changes the list: a new backup, a check result, a failed task.
  const active = !!running;
  useEffect(() => void client.invalidateQueries({ queryKey: ["backups"] }), [active, client]);
  const restart = async (action: Wait["action"], request: () => Promise<{ attempt: string }>) => {
    setFailure(null);
    setApplied(null);
    try {
      const before = (await body(api.GET("/api/v1/status"))).started_ms;
      const { attempt } = await request();
      const mine: Wait = { action, attempt };
      setWait(mine);
      const result = await waitForRestart(before, {
        attempt,
        onProgress: (live) => setWait({ ...mine, live }),
      });
      if (result === "signed-out") {
        window.location.reload();
        return;
      }
      setApplied(mine);
      await client.invalidateQueries();
    } catch (e) {
      setFailure(e instanceof Error ? e : new Error(String(e)));
      done();
    } finally {
      setWait(null);
    }
  };
  const restore = (name: string) =>
    void restart("restore", () => body(api.POST("/api/v1/backups/{name}/restore", { params: { path: { name } } })));
  const undo = () => void restart("undo", () => body(api.POST("/api/v1/backups/undo")));
  const dropUndo = useMutation({ mutationFn: () => body(api.DELETE("/api/v1/backups/undo")), onSettled: done });
  const dismiss = useMutation({ mutationFn: () => body(api.DELETE("/api/v1/backups/last-restore")), onSettled: done });
  const dropLegacy = useMutation({ mutationFn: () => body(api.DELETE("/api/v1/backups/legacy")), onSettled: done });
  const [legacyOpen, setLegacyOpen] = useState(false);

  const free = v?.free_bytes;
  const need = (v?.database_bytes ?? 0) * value.keep;
  const last = v?.last_restore;

  const setBackup = (backup: Partial<Doc["maintenance"]["backup"]>) => onChange({ ...maintenance, backup: { ...value, ...backup } });
  const upload = useUpload(busy, done);

  return (
    <SettingsSection title={title} description={description} actions={!v?.unavailable_reason && upload.button}>
      {v?.unavailable_reason ? (
        <Empty title="No backups for this database">{v.unavailable_reason}</Empty>
      ) : (
        <>
          {wait && (
            <Alert>
              <Loader2 className="animate-spin" />
              <AlertTitle>{wait.action === "restore" ? "Restoring" : "Undoing the restore"}</AlertTitle>
              <AlertDescription>
                <p>
                  {wait.live
                    ? `${wait.live.step}${wait.live.total ? `: ${bytes(wait.live.done)} / ${bytes(wait.live.total)}` : ""}.`
                    : "Waiting for infinidysk to restart."}
                </p>
                <p>infinidysk restarts when it is ready; this page reloads its data then.</p>
              </AlertDescription>
            </Alert>
          )}
          {failure && <Failure error={failure} />}
          {applied?.action === "restore" && v && last?.attempt !== applied.attempt && (
            <Alert variant="destructive">
              <CircleAlert />
              <AlertDescription>infinidysk restarted, but the restore was not applied. Check the logs.</AlertDescription>
            </Alert>
          )}
          {last && (last.failed || last.undo_failed || v?.undo) && (
            <Alert variant={last.failed || last.undo_failed ? "destructive" : "default"}>
              {last.failed || last.undo_failed ? <CircleAlert /> : <RotateCcw />}
              <AlertDescription className="space-y-2">
                {last.failed ? (
                  <>
                    <p>The restore of {last.backup} did not open, so infinidysk kept the database you had.</p>
                    <p className="font-mono text-xs break-words">{last.failed}</p>
                  </>
                ) : (
                  <p>
                    Restored {last.backup}
                    {last.backup_created_ms != null && <> from {stamp(last.backup_created_ms)}</>}, {ago(last.at_ms)}.
                  </p>
                )}
                {last.undo_failed && (
                  <>
                    <p>Undo did not open the earlier database, so infinidysk kept the restored one. The undo copy is still there.</p>
                    <p className="font-mono text-xs break-words">{last.undo_failed}</p>
                  </>
                )}
                <div className="flex flex-wrap gap-2">
                  {!last.failed && v?.undo && (
                    <>
                      <ConfirmButton
                        variant="outline"
                        label="Undo restore"
                        disabled={busy}
                        title="Undo the restore?"
                        message="Put back the database you had before the restore? infinidysk restarts. Changes made since the restore are lost. The restored copy is deleted; the backup itself stays."
                        confirm="Undo restore"
                        onConfirm={undo}
                      />
                      <ConfirmButton
                        label={`Delete undo copy (${bytes(v.undo.size_bytes)})`}
                        disabled={busy || dropUndo.isPending}
                        title="Delete the undo copy?"
                        message="Deletes the database you had before the restore. You can no longer undo it."
                        confirm="Delete undo copy"
                        onConfirm={() => dropUndo.mutate()}
                      />
                    </>
                  )}
                  {(last.failed || !v?.undo) && (
                    <Button type="button" variant="outline" size="sm" disabled={dismiss.isPending} onClick={() => dismiss.mutate()}>
                      Dismiss
                    </Button>
                  )}
                </div>
                {dropUndo.isError && <Failure error={dropUndo.error} />}
                {dismiss.isError && <Failure error={dismiss.error} />}
              </AlertDescription>
            </Alert>
          )}
          {v?.task && (
            <Alert variant="destructive">
              <CircleAlert />
              <AlertTitle>
                {v.task.action === "verify" ? "Verify failed" : v.task.action === "undo" ? "Undo stopped" : "Restore stopped"}
              </AlertTitle>
              <AlertDescription>
                <p>
                  {v.task.name}, {ago(v.task.finished_ms)}
                </p>
                <p className="font-mono text-xs break-words">{v.task.reason}</p>
              </AlertDescription>
            </Alert>
          )}
          {v?.legacy && (
            <Alert>
              <CircleAlert />
              <AlertDescription className="space-y-2">
                <p>
                  {v.legacy.folders.toLocaleString()} backups from infinidysk 1.x use {bytes(v.legacy.bytes)}. 2.x cannot restore them.
                </p>
                <Button type="button" variant="outline" size="sm" disabled={dropLegacy.isPending} onClick={() => setLegacyOpen(true)}>
                  Delete 1.x backups
                </Button>
                {dropLegacy.isError && <Failure error={dropLegacy.error} />}
              </AlertDescription>
            </Alert>
          )}
          <TextConfirmDialog
            open={legacyOpen}
            onOpenChange={setLegacyOpen}
            title="Delete 1.x backups?"
            message={`Deletes ${v?.legacy?.folders ?? 0} backup folders from infinidysk 1.x (${bytes(v?.legacy?.bytes ?? 0)}). They cannot be brought back.`}
            expected="delete"
            confirm="Delete 1.x backups"
            onConfirm={() => dropLegacy.mutate()}
          />

          {jobsView.isError && <Failure error={jobsView.error} />}
          {job && (
            <JobCard
              job={job}
              running={running}
              scheduled={job.always_scheduled || maintenance.scheduled.includes(job.job)}
              locked={locked("maintenance.scheduled")}
              onScheduled={(on) =>
                onChange({
                  ...maintenance,
                  scheduled: on ? [...maintenance.scheduled, job.job] : maintenance.scheduled.filter((s) => s !== job.job),
                })
              }
            />
          )}

          <section aria-labelledby="backup-list" className="space-y-2">
            <h3 id="backup-list" className="text-muted-foreground text-xs font-medium tracking-wide uppercase">
              Saved backups
            </h3>
            {upload.status}
            {view.isError ? (
              <Failure error={view.error} />
            ) : !v ? (
              <p className="text-muted-foreground text-xs">Loading…</p>
            ) : v.backups.length > 0 ? (
              <BackupList backups={v.backups} busy={busy} onRestore={restore} onChanged={done} />
            ) : (
              <Empty title="No backups yet">Run the backup now, turn on Run daily, or upload a backup you downloaded.</Empty>
            )}
          </section>

          <SettingsPanel title="Storage">
            <Field
              id="backup-keep"
              layout="row"
              label="Copies to keep"
              hint="Scheduled and manual backups. Pinned, uploaded and added backups do not count."
            >
              <Input
                id="backup-keep"
                type="number"
                min={1}
                max={100}
                required
                disabled={locked("maintenance.backup.keep")}
                value={value.keep}
                onChange={(e) => setBackup({ keep: Number(e.target.value) })}
              />
              {v && v.database_bytes > 0 && (
                <p className={free != null && need > free ? "text-warning-text text-xs" : "text-muted-foreground text-xs"}>
                  About {bytes(v.database_bytes)} each, {bytes(need)} in all
                  {free != null && <>; {bytes(free)} free</>}.
                </p>
              )}
            </Field>
            <Field
              id="backup-dir"
              label="Backup folder"
              error={v?.dir_error}
              hint="Changing the folder does not move existing backups. Files you copy in must end in .sqlite and use letters, digits, spaces, dots, dashes or underscores; they then show up in the list."
            >
              <Input
                id="backup-dir"
                className="font-mono"
                placeholder={v?.dir}
                disabled={locked("maintenance.backup.dir")}
                value={value.dir}
                onChange={(e) => setBackup({ dir: e.target.value })}
              />
            </Field>
          </SettingsPanel>
        </>
      )}
    </SettingsSection>
  );
}

function BackupList({
  backups,
  busy,
  onRestore,
  onChanged,
}: {
  backups: Backup[];
  busy: boolean;
  onRestore: (name: string) => void;
  onChanged: () => void;
}) {
  const [restoring, setRestoring] = useState<Backup | null>(null);
  const [noting, setNoting] = useState<Backup | null>(null);
  const [error, setError] = useState<Error | null>(null);
  const run = (request: Promise<unknown>) => {
    setError(null);
    request.catch((e: Error) => setError(e)).finally(onChanged);
  };
  const path = (name: string) => ({ params: { path: { name } } });
  return (
    <div className="space-y-2">
      <Panel>
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Created</TableHead>
              <TableHead className="hidden xl:table-cell">Kind</TableHead>
              <TableHead className="hidden text-right xl:table-cell">Size</TableHead>
              <TableHead className="hidden text-right xl:table-cell">Items</TableHead>
              <TableHead className="hidden xl:table-cell">Check</TableHead>
              <TableHead className="w-0">
                <span className="sr-only">Actions</span>
              </TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {backups.map((b) => {
              const [label, tone, tip] = check(b);
              return (
                <TableRow key={b.name}>
                  <TableCell className="max-w-0 min-w-0 whitespace-normal">
                    <div className="flex items-center gap-1.5">
                      <span className="tabular-nums" title={stamp(b.created_ms)}>
                        {ago(b.created_ms)}
                      </span>
                      {b.pinned && <Pin className="text-muted-foreground size-3.5 shrink-0" aria-label="Pinned" />}
                    </div>
                    <div className="text-muted-foreground truncate font-mono text-xs" title={b.source_name ?? b.name}>
                      {b.name}
                    </div>
                    {b.note && <div className="text-muted-foreground text-xs break-words">{b.note}</div>}
                    <div className="text-muted-foreground mt-1 flex flex-wrap items-center gap-x-2 gap-y-1 text-xs xl:hidden">
                      <Badge variant="outline">{kinds[b.kind]}</Badge>
                      <span>{bytes(b.size_bytes)}</span>
                      <span title={tip}>
                        <Status tone={tone}>{label}</Status>
                      </span>
                    </div>
                  </TableCell>
                  <TableCell className="hidden xl:table-cell">
                    <Badge variant="outline">{kinds[b.kind]}</Badge>
                  </TableCell>
                  <TableCell className="hidden text-right tabular-nums xl:table-cell">{bytes(b.size_bytes)}</TableCell>
                  <TableCell className="hidden text-right tabular-nums xl:table-cell">{b.items?.toLocaleString() ?? "–"}</TableCell>
                  <TableCell className="hidden xl:table-cell">
                    <span title={tip}>
                      <Status tone={tone}>{label}</Status>
                    </span>
                  </TableCell>
                  <TableCell>
                    <RowActions
                      name={b.name}
                      actions={[
                        { label: "Download", icon: <Download />, href: fileUrl(b.name) },
                        {
                          label: "Verify",
                          icon: <ShieldCheck />,
                          disabled: busy,
                          onSelect: () => run(body(api.POST("/api/v1/backups/{name}/verify", path(b.name)))),
                        },
                        {
                          label: "Restore…",
                          icon: <RotateCcw />,
                          disabled: busy || b.incomplete || b.newer,
                          onSelect: () => setRestoring(b),
                        },
                        {
                          label: b.pinned ? "Unpin" : "Pin",
                          icon: b.pinned ? <PinOff /> : <Pin />,
                          onSelect: () => run(body(api.PATCH("/api/v1/backups/{name}", { ...path(b.name), body: { pinned: !b.pinned } }))),
                        },
                        { label: "Edit note", icon: <NotebookPen />, onSelect: () => setNoting(b) },
                        {
                          label: "Delete",
                          icon: <Trash2 />,
                          disabled: busy,
                          onSelect: () => run(body(api.DELETE("/api/v1/backups/{name}", path(b.name)))),
                          confirm: {
                            title: "Delete this backup?",
                            message: b.pinned
                              ? `${b.name} is pinned. Deleting it cannot be undone.`
                              : `Deletes ${b.name}. This cannot be undone.`,
                            confirm: "Delete backup",
                          },
                        },
                      ]}
                    />
                  </TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
      </Panel>
      {error && <Failure error={error} />}
      <TextConfirmDialog
        open={!!restoring}
        onOpenChange={(open) => !open && setRestoring(null)}
        title="Restore this backup?"
        message={
          restoring &&
          `Restore ${restoring.name} from ${stamp(restoring.created_ms)}? infinidysk restarts into this backup. Settings, logins, downloads and history go back to how they were then; anything added since is gone, and imports queued then start again. You sign in with the password you had then. The current database is kept as an undo copy and replaces any earlier undo copy. Afterwards, run Remove dead links to clean up library links to downloads added since the backup.`
        }
        expected="restore"
        confirm="Restore"
        onConfirm={() => {
          if (restoring) onRestore(restoring.name);
          setRestoring(null);
        }}
      />
      {noting && (
        <NoteDialog
          backup={noting}
          onClose={() => setNoting(null)}
          onSave={async (note) => {
            await body(api.PATCH("/api/v1/backups/{name}", { ...path(noting.name), body: { note } }));
            setNoting(null);
            onChanged();
          }}
        />
      )}
    </div>
  );
}

function NoteDialog({ backup, onClose, onSave }: { backup: Backup; onClose: () => void; onSave: (note: string) => Promise<void> }) {
  const [note, setNote] = useState(backup.note);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<Error | null>(null);
  const guard = useDiscardGuard(note !== backup.note, onClose);
  return (
    <Dialog open onOpenChange={(open) => !open && !saving && guard.requestClose()}>
      <DialogContent>
        <form
          className="space-y-4"
          onSubmit={async (e) => {
            e.preventDefault();
            setSaving(true);
            setError(null);
            try {
              await onSave(note);
            } catch (err) {
              setError(err instanceof Error ? err : new Error(String(err)));
            } finally {
              setSaving(false);
            }
          }}
        >
          <DialogHeader>
            <DialogTitle>Edit note</DialogTitle>
            <DialogDescription className="break-words">{backup.name}</DialogDescription>
          </DialogHeader>
          <Field id="backup-note" label="Note" hint={`${note.length} / ${NOTE_MAX}`}>
            <Textarea id="backup-note" maxLength={NOTE_MAX} value={note} onChange={(e) => setNote(e.target.value)} />
          </Field>
          {error && <Failure error={error} />}
          <DialogFooter>
            <Button type="button" variant="outline" disabled={saving} onClick={guard.requestClose}>
              Cancel
            </Button>
            <Button disabled={saving}>Save note</Button>
          </DialogFooter>
        </form>
      </DialogContent>
      {guard.dialog}
    </Dialog>
  );
}

/** Uploads with `XMLHttpRequest`: `fetch` reports no upload progress. */
function useUpload(busy: boolean, onUploaded: () => void) {
  const input = useRef<HTMLInputElement>(null);
  const [file, setFile] = useState<File | null>(null);
  const [sent, setSent] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);
  const upload = (picked: File) => {
    setFile(picked);
    setError(null);
    setSent(0);
    const xhr = new XMLHttpRequest();
    xhr.open("PUT", `/api/v1/backups/upload?name=${encodeURIComponent(picked.name)}`);
    xhr.setRequestHeader("Content-Type", "application/octet-stream");
    xhr.upload.onprogress = (e) => setSent(e.loaded);
    xhr.onload = () => {
      setSent(null);
      if (xhr.status === 201) {
        setFile(null);
        onUploaded();
        return;
      }
      let message = xhr.statusText || `HTTP ${xhr.status}`;
      try {
        message = (JSON.parse(xhr.responseText) as { error?: string }).error ?? message;
      } catch {
        // A proxy's error page is not JSON; keep the status text.
      }
      setError(message);
    };
    xhr.onerror = () => {
      setSent(null);
      setError("The upload did not reach infinidysk.");
    };
    xhr.send(picked);
  };
  const percent = file && sent != null && file.size > 0 ? (sent / file.size) * 100 : 0;
  const button = (
    <>
      <input
        ref={input}
        type="file"
        accept=".sqlite"
        hidden
        aria-label="Backup file"
        onChange={(e) => {
          const picked = e.target.files?.[0];
          e.target.value = "";
          if (picked) upload(picked);
        }}
      />
      <Button
        type="button"
        variant="outline"
        title="A .sqlite file from a backup download. It shows up in the list; restore checks it first."
        disabled={busy || sent != null}
        onClick={() => input.current?.click()}
      >
        {sent != null ? <Loader2 className="animate-spin" /> : <Upload />}
        Upload backup
      </Button>
    </>
  );
  const status = (
    <>
      {file && sent != null && (
        <div className="space-y-1">
          <div className="text-muted-foreground flex justify-between gap-2 text-xs tabular-nums">
            <span className="min-w-0 truncate">Uploading {file.name}</span>
            <span className="shrink-0">
              {bytes(sent)} / {bytes(file.size)}
            </span>
          </div>
          <div className="bg-muted h-1.5 overflow-hidden rounded-full" role="progressbar" aria-valuenow={Math.round(percent)}>
            <div className="bg-primary h-full transition-[width]" style={{ width: `${percent}%` }} />
          </div>
        </div>
      )}
      {error && (
        <Alert variant="destructive">
          <CircleAlert />
          <AlertDescription>
            <p className="wrap-anywhere">
              Uploading {file?.name} failed: {error}
            </p>
            <p className="text-xs">{UPLOAD_HINT}</p>
          </AlertDescription>
        </Alert>
      )}
    </>
  );
  return { button, status };
}
