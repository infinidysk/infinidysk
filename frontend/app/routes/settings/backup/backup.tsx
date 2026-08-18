import { useCallback, useEffect, useRef, useState, type Dispatch, type SetStateAction } from "react";
import { ConfirmModal } from "~/components/confirm-modal/confirm-modal";
import {
    decideMigrationStatusPoll,
    MigrationProgressView,
    MigrationShell,
    type MigrationStatus,
} from "~/components/migration-progress";
import { Button } from "~/components/ui/button";
import { Alert, Badge, Spinner, Tooltip } from "~/components/ui/feedback";
import { Checkbox, Input, Select, Toggle } from "~/components/ui/form";
import { Icon } from "~/components/ui/icon";
import { ManagedSetting, SettingsIntro, SettingsPage } from "~/components/ui";
import { useWebsocketTopic } from "~/utils/shared-websocket";
import { withUrlBase } from "~/utils/url-base";

type BackupSettingsProps = {
    config: Record<string, string>;
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>;
};

type BackupFileEntry = { name: string; bytes: number };

type BackupManifest = {
    id: string;
    createdAt: string;
    kind: string;
    notes: string;
    preserved: boolean;
    appVersion?: string | null;
    lastMainMigration?: string | null;
    files: BackupFileEntry[];
};

type LastRestoreReport = {
    backupId: string;
    restoredAt: string;
    missingBlobRefs: number;
    checkedRefs: number;
};

type BackupListResponse = {
    status?: boolean;
    backups?: BackupManifest[];
    taskRunning?: boolean;
    pendingRestore?: boolean;
    lastRestoreReport?: LastRestoreReport | null;
    mainDatabaseProvider?: string;
    mainDatabaseBackupSupported?: boolean;
    error?: string;
};

// Error body shared by the /api/db-backup-* and /api/db-restore endpoints (BaseApiResponse).
type BackupErrorResponse = { error?: string };

export function BackupSettings({ config, setNewConfig }: BackupSettingsProps) {
    const [backups, setBackups] = useState<BackupManifest[]>([]);
    const [taskRunning, setTaskRunning] = useState(false);
    const [lastRestoreReport, setLastRestoreReport] = useState<LastRestoreReport | null>(null);
    const [mainDatabaseProvider, setMainDatabaseProvider] = useState<string | null>(null);
    const [reportDismissed, setReportDismissed] = useState(false);
    const [listError, setListError] = useState<string | null>(null);
    const [busy, setBusy] = useState<string | null>(null);
    const [message, setMessage] = useState<{ text: string; variant: "success" | "danger" | "warning" } | null>(null);
    const [notes, setNotes] = useState("");
    const [backupProgress, setBackupProgress] = useState<string | null>(null);
    const [restoreProgress, setRestoreProgress] = useState<string | null>(null);
    const [wsConnected, setWsConnected] = useState(false);
    const [confirmDeleteId, setConfirmDeleteId] = useState<string | null>(null);
    const [confirmRestoreId, setConfirmRestoreId] = useState<string | null>(null);
    const [restoring, setRestoring] = useState(false);
    const [migrationStatus, setMigrationStatus] = useState<MigrationStatus | null>(null);
    const [restorePhase, setRestorePhase] = useState<"idle" | "staging" | "restarting" | "migrating">("idle");
    const fileInputRef = useRef<HTMLInputElement>(null);
    const reloadScheduled = useRef(false);

    const refreshList = useCallback(async () => {
        try {
            const res = await fetch(withUrlBase("/api/db-backup-list"), { cache: "no-store" });
            const data = (await res.json().catch(() => null)) as BackupListResponse | null;
            if (!res.ok) {
                setListError(data?.error || `Failed to load backups (${res.status})`);
                return;
            }
            setBackups(data?.backups ?? []);
            setTaskRunning(!!data?.taskRunning);
            setLastRestoreReport(data?.lastRestoreReport ?? null);
            setMainDatabaseProvider(data?.mainDatabaseProvider ?? null);
            setListError(null);
        } catch {
            setListError("Failed to load backups.");
        }
    }, []);

    useEffect(() => {
        void refreshList();
    }, [refreshList]);

    useWebsocketTopic("dbbk", "state", setBackupProgress, {
        onOpen: () => setWsConnected(true),
        onClose: () => setWsConnected(false),
    });

    useWebsocketTopic("dbrs", "state", setRestoreProgress, {
        onOpen: () => setWsConnected(true),
    });

    useEffect(() => {
        if (!message || message.variant !== "success") return;
        const timer = window.setTimeout(() => setMessage(null), 4000);
        return () => window.clearTimeout(timer);
    }, [message]);

    useEffect(() => {
        if (!backupProgress) return;
        if (backupProgress.startsWith("Completed") || backupProgress.startsWith("Failed")) {
            void refreshList();
            setBusy(null);
        }
    }, [backupProgress, refreshList]);

    // Poll migration status while a restore restart is in flight.
    useEffect(() => {
        if (restorePhase !== "restarting" && restorePhase !== "migrating") return;

        let cancelled = false;
        const poll = async () => {
            try {
                const res = await fetch(withUrlBase("/api/migration-status"), {
                    headers: { accept: "application/json" },
                    cache: "no-store",
                });
                if (cancelled) return;
                const body: unknown = res.ok ? await res.json().catch(() => null) : null;
                const decision = decideMigrationStatusPoll(res.status, body);
                if (decision.action === "migrating") {
                    setMigrationStatus(decision.status);
                    setRestorePhase("migrating");
                    if (decision.reloadMs !== undefined && !reloadScheduled.current) {
                        reloadScheduled.current = true;
                        window.setTimeout(() => window.location.reload(), decision.reloadMs);
                    }
                    return;
                }
                if (decision.action === "connecting") {
                    setRestorePhase("restarting");
                }
            } catch {
                if (!cancelled) setRestorePhase("restarting");
            }
        };

        void poll();
        const interval = window.setInterval(() => void poll(), 2000);
        return () => {
            cancelled = true;
            window.clearInterval(interval);
        };
    }, [restorePhase]);

    const createBackup = useCallback(async () => {
        setBusy("create");
        setMessage(null);
        setBackupProgress(null);
        try {
            const form = new FormData();
            if (notes.trim()) form.append("notes", notes.trim());
            const res = await fetch(withUrlBase("/api/db-backup-create"), { method: "POST", body: form });
            if (res.status === 409) {
                setMessage({ text: "A backup or restore task is already running.", variant: "warning" });
                return;
            }
            if (!res.ok) {
                const data = (await res.json().catch(() => null)) as BackupErrorResponse | null;
                setMessage({ text: data?.error || `Backup failed (${res.status})`, variant: "danger" });
                return;
            }
            setNotes("");
            setMessage({ text: "Backup started.", variant: "success" });
            await refreshList();
        } catch {
            setMessage({ text: "Backup request failed.", variant: "danger" });
        } finally {
            setBusy(null);
        }
    }, [notes, refreshList]);

    const updateBackup = useCallback(async (id: string, patch: { preserved?: boolean; notes?: string }) => {
        const form = new FormData();
        form.append("id", id);
        if (patch.preserved !== undefined) form.append("preserved", String(patch.preserved));
        if (patch.notes !== undefined) form.append("notes", patch.notes);
        const res = await fetch(withUrlBase("/api/db-backup-update"), { method: "POST", body: form });
        if (!res.ok) {
            const data = (await res.json().catch(() => null)) as BackupErrorResponse | null;
            setMessage({ text: data?.error || `Update failed (${res.status})`, variant: "danger" });
            return;
        }
        await refreshList();
    }, [refreshList]);

    const deleteBackup = useCallback(async (id: string) => {
        setConfirmDeleteId(null);
        setBusy(`delete-${id}`);
        try {
            const form = new FormData();
            form.append("id", id);
            const res = await fetch(withUrlBase("/api/db-backup-delete"), { method: "POST", body: form });
            if (!res.ok) {
                const data = (await res.json().catch(() => null)) as BackupErrorResponse | null;
                setMessage({ text: data?.error || `Delete failed (${res.status})`, variant: "danger" });
                return;
            }
            setMessage({ text: "Backup deleted.", variant: "success" });
            await refreshList();
        } catch {
            setMessage({ text: "Delete failed.", variant: "danger" });
        } finally {
            setBusy(null);
        }
    }, [refreshList]);

    const downloadBackup = useCallback(async (id: string) => {
        setBusy(`download-${id}`);
        setMessage(null);
        try {
            const res = await fetch(withUrlBase(`/api/db-backup-download?id=${encodeURIComponent(id)}`));
            if (!res.ok) {
                const data = (await res.json().catch(() => null)) as BackupErrorResponse | null;
                setMessage({ text: data?.error || `Download failed (${res.status})`, variant: "danger" });
                return;
            }
            const blob = await res.blob();
            const url = URL.createObjectURL(blob);
            const a = document.createElement("a");
            a.href = url;
            a.download = `nzbdav-backup-${id}.zip`;
            document.body.appendChild(a);
            a.click();
            a.remove();
            URL.revokeObjectURL(url);
        } catch {
            setMessage({ text: "Download failed.", variant: "danger" });
        } finally {
            setBusy(null);
        }
    }, []);

    const uploadBackup = useCallback(async (file: File) => {
        setBusy("upload");
        setMessage(null);
        try {
            const form = new FormData();
            form.append("file", file);
            const res = await fetch(withUrlBase("/api/db-backup-upload"), { method: "POST", body: form });
            const data = (await res.json().catch(() => null)) as BackupErrorResponse | null;
            if (!res.ok) {
                setMessage({ text: data?.error || `Upload failed (${res.status})`, variant: "danger" });
                return;
            }
            setMessage({ text: "Backup uploaded.", variant: "success" });
            await refreshList();
        } catch {
            setMessage({ text: "Upload failed.", variant: "danger" });
        } finally {
            setBusy(null);
            if (fileInputRef.current) fileInputRef.current.value = "";
        }
    }, [refreshList]);

    const startRestore = useCallback(async (id: string, acknowledged?: boolean) => {
        if (!acknowledged) {
            setMessage({ text: "You must acknowledge the restore warning.", variant: "warning" });
            return;
        }
        setConfirmRestoreId(null);
        setBusy(`restore-${id}`);
        setRestoreProgress(null);
        setRestoring(true);
        setRestorePhase("staging");
        setMessage(null);
        try {
            const form = new FormData();
            form.append("id", id);
            const res = await fetch(withUrlBase("/api/db-restore"), { method: "POST", body: form });
            const data = (await res.json().catch(() => null)) as BackupErrorResponse | null;
            if (res.status === 409) {
                setMessage({ text: "A backup or restore task is already running.", variant: "warning" });
                setRestoring(false);
                setRestorePhase("idle");
                return;
            }
            if (!res.ok) {
                setMessage({ text: data?.error || `Restore failed (${res.status})`, variant: "danger" });
                setRestoring(false);
                setRestorePhase("idle");
                return;
            }
            setRestorePhase("restarting");
        } catch {
            // Expected once the backend exits for restart.
            setRestorePhase("restarting");
        } finally {
            setBusy(null);
        }
    }, []);

    if (restoring && (restorePhase === "restarting" || restorePhase === "migrating")) {
        if (restorePhase === "migrating" && migrationStatus) {
            return (
                <div className="fixed inset-0 z-50 overflow-auto bg-base-300">
                    <MigrationProgressView status={migrationStatus} />
                </div>
            );
        }
        return (
            <div className="fixed inset-0 z-50 overflow-auto bg-base-300">
                <MigrationShell
                    title="Applying database restore"
                    subtitle="The backend is restarting into maintenance mode to swap databases. This page will update automatically."
                >
                    <div className="flex items-center gap-3 text-sm text-base-content/70">
                        <span className="loading loading-spinner loading-sm text-primary" />
                        <span>{restoreProgress || "Waiting for maintenance status…"}</span>
                    </div>
                </MigrationShell>
            </div>
        );
    }

    const scheduleEnabled = config["backup.schedule-enabled"] === "true";
    const scheduleTime = getScheduledTime(config);

    return (
        <SettingsPage>
            <SettingsIntro>
                Create logical SQL dumps of your databases, schedule automatic backups, and restore from a previous
                snapshot when needed.
            </SettingsIntro>

            {mainDatabaseProvider === "postgres" && (
                <Alert className="alert-soft items-start py-3 text-sm" variant="warning">
                    <Icon name="info" className="!text-[20px]" />
                    <div>
                        <p className="font-semibold">PostgreSQL main database is externally managed</p>
                        <p className="mt-0.5 text-xs opacity-80">
                            This page backs up and restores only the local SQLite metrics and warden databases. The
                            main PostgreSQL database is not included; use your PostgreSQL provider&apos;s backup and
                            restore tooling for it.
                        </p>
                    </div>
                </Alert>
            )}

            {!reportDismissed && lastRestoreReport && lastRestoreReport.missingBlobRefs > 0 && (
                <Alert className="alert-soft items-start py-3 text-sm" variant="warning">
                    <Icon name="warning" className="!text-[20px]" />
                    <div className="flex min-w-0 flex-1 items-start justify-between gap-3">
                        <div>
                            <p className="font-semibold">Missing blobs after restore</p>
                            <p className="mt-0.5 text-xs opacity-80">
                                Restore of <span className="font-mono">{lastRestoreReport.backupId}</span> found{" "}
                                <strong>{lastRestoreReport.missingBlobRefs}</strong> of{" "}
                                {lastRestoreReport.checkedRefs} blob references pointing to missing files under{" "}
                                <code className="font-mono">blobs/</code>. Affected items may fail to stream until
                                re-downloaded.
                            </p>
                        </div>
                        <Button variant="ghost" size="xsmall" onClick={() => setReportDismissed(true)}>
                            Dismiss
                        </Button>
                    </div>
                </Alert>
            )}

            <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
                <ManagedSetting configKeys={["backup.schedule-enabled", "backup.schedule-time"]}>
                <section className="overflow-hidden rounded-lg border border-base-content/10 bg-base-100">
                    <div className="flex items-start gap-3 border-b border-base-content/10 p-4">
                        <span className="rounded-lg bg-primary/10 p-2 text-primary">
                            <Icon name="event_repeat" className="!text-[20px]" />
                        </span>
                        <div>
                            <h2 className="text-sm font-semibold text-base-content">Scheduled backups</h2>
                            <p className="mt-0.5 text-xs leading-relaxed text-base-content/50">
                                Create a database dump automatically once per day.
                            </p>
                        </div>
                    </div>

                    <div className="space-y-4 p-4">
                        <Tooltip placement="bottom" content="Writes a logical .sql dump of all databases under the config volume once per day.">
                            <Toggle
                                id="backup-schedule-enabled"
                                className="cursor-pointer gap-2 rounded-lg bg-base-200/40 p-3"
                                checked={scheduleEnabled}
                                onChange={(e) =>
                                    setNewConfig({
                                        ...config,
                                        "backup.schedule-enabled": String(e.target.checked),
                                    })
                                }
                                label={<span className="text-sm font-medium text-base-content">Enable daily backup</span>}
                            />
                        </Tooltip>

                        <fieldset className="space-y-2" disabled={!scheduleEnabled}>
                            <legend className="text-xs font-medium uppercase tracking-wide text-base-content/50">
                                Daily run time
                            </legend>
                            <div className="grid grid-cols-3 gap-2">
                                <label className="space-y-1">
                                    <span className="block text-[11px] text-base-content/45">Hour</span>
                                    <Select
                                        className="w-full"
                                        aria-label="Backup hour"
                                        value={scheduleTime.hour}
                                        onChange={(e) =>
                                            setNewConfig({
                                                ...config,
                                                "backup.schedule-time": buildScheduledTime(
                                                    parseInt(e.target.value),
                                                    scheduleTime.minute,
                                                    scheduleTime.period,
                                                ),
                                            })
                                        }
                                    >
                                        {Array.from({ length: 12 }, (_, i) => i + 1).map((h) => (
                                            <option key={h} value={h}>
                                                {h}
                                            </option>
                                        ))}
                                    </Select>
                                </label>
                                <label className="space-y-1">
                                    <span className="block text-[11px] text-base-content/45">Minute</span>
                                    <Select
                                        className="w-full"
                                        aria-label="Backup minute"
                                        value={scheduleTime.minute}
                                        onChange={(e) =>
                                            setNewConfig({
                                                ...config,
                                                "backup.schedule-time": buildScheduledTime(
                                                    scheduleTime.hour,
                                                    parseInt(e.target.value),
                                                    scheduleTime.period,
                                                ),
                                            })
                                        }
                                    >
                                        <option value={0}>00</option>
                                        <option value={15}>15</option>
                                        <option value={30}>30</option>
                                        <option value={45}>45</option>
                                    </Select>
                                </label>
                                <label className="space-y-1">
                                    <span className="block text-[11px] text-base-content/45">Period</span>
                                    <Select
                                        className="w-full"
                                        aria-label="Backup period"
                                        value={scheduleTime.period}
                                        onChange={(e) =>
                                            setNewConfig({
                                                ...config,
                                                "backup.schedule-time": buildScheduledTime(
                                                    scheduleTime.hour,
                                                    scheduleTime.minute,
                                                    e.target.value as "am" | "pm",
                                                ),
                                            })
                                        }
                                    >
                                        <option value="am">AM</option>
                                        <option value="pm">PM</option>
                                    </Select>
                                </label>
                            </div>
                        </fieldset>

                        <div className="flex items-start gap-2 rounded-lg bg-base-200/30 px-3 py-2.5 text-xs leading-relaxed text-base-content/55">
                            <Icon name="schedule" className="mt-0.5 !text-[17px] shrink-0 text-base-content/45" />
                            <p>
                                Schedule times use the server timezone. Set{" "}
                                <code className="font-mono text-base-content/70">TZ</code> in the container environment
                                if the displayed time does not match your location.
                            </p>
                        </div>
                    </div>
                </section>
                </ManagedSetting>

                <ManagedSetting configKeys={["backup.retention-count"]}>
                <section className="overflow-hidden rounded-lg border border-base-content/10 bg-base-100">
                    <div className="flex items-start gap-3 border-b border-base-content/10 p-4">
                        <span className="rounded-lg bg-primary/10 p-2 text-primary">
                            <Icon name="inventory_2" className="!text-[20px]" />
                        </span>
                        <div>
                            <h2 className="text-sm font-semibold text-base-content">Retention</h2>
                            <p className="mt-0.5 text-xs leading-relaxed text-base-content/50">
                                Control how many non-preserved backups are kept automatically.
                            </p>
                        </div>
                    </div>

                    <div className="space-y-4 p-4">
                        <div className="space-y-2">
                            <label className="block text-sm font-medium text-base-content" htmlFor="backup-retention-count">
                                Keep newest backups
                            </label>
                            <div className="flex items-center gap-2">
                                <Input
                                    id="backup-retention-count"
                                    type="number"
                                    min={0}
                                    value={config["backup.retention-count"] ?? "5"}
                                    onChange={(e) =>
                                        setNewConfig({
                                            ...config,
                                            "backup.retention-count": e.target.value,
                                        })
                                    }
                                    className="w-full max-w-[8rem]"
                                />
                                <span className="text-xs text-base-content/45">count</span>
                            </div>
                            <p className="text-[11px] leading-relaxed text-base-content/45">
                                Prunes older non-preserved backups. Set to 0 to disable pruning.
                            </p>
                        </div>

                        <div className="flex items-start gap-2 rounded-lg bg-base-200/30 px-3 py-2.5 text-xs leading-relaxed text-base-content/55">
                            <Icon name="lock" className="mt-0.5 !text-[17px] shrink-0 text-base-content/45" />
                            <p>
                                <span className="font-medium text-base-content/70">Preserved backups are never pruned.</span>
                                {" "}Mark important snapshots as preserved in the list below.
                            </p>
                        </div>
                    </div>
                </section>
                </ManagedSetting>
            </div>

            <section className="overflow-hidden rounded-lg border border-base-content/10 bg-base-100">
                <div className="flex items-start gap-3 border-b border-base-content/10 p-4">
                    <span className="rounded-lg bg-primary/10 p-2 text-primary">
                        <Icon name="backup" className="!text-[20px]" />
                    </span>
                    <div>
                        <h2 className="text-sm font-semibold text-base-content">Create or upload</h2>
                        <p className="mt-0.5 text-xs leading-relaxed text-base-content/50">
                            Start a backup now or import a previously downloaded archive.
                        </p>
                    </div>
                </div>

                <div className="space-y-4 p-4">
                    <Input
                        placeholder="Optional notes for this backup"
                        value={notes}
                        onChange={(e) => setNotes(e.target.value)}
                    />

                    <div className="rounded-lg border border-base-content/10 bg-base-200/40 p-3">
                        <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
                            <div className="flex flex-wrap items-center gap-2">
                                <Button
                                    className="shrink-0"
                                    variant="primary"
                                    disabled={busy === "create" || taskRunning}
                                    onClick={() => void createBackup()}
                                >
                                    {busy === "create"
                                        ? <Spinner className="h-4 w-4" />
                                        : <Icon name="backup" className="!text-[18px]" />}
                                    Create Backup
                                </Button>
                                <Button
                                    className="shrink-0"
                                    variant="outline"
                                    disabled={busy === "upload"}
                                    onClick={() => fileInputRef.current?.click()}
                                >
                                    {busy === "upload"
                                        ? <Spinner className="h-4 w-4" />
                                        : <Icon name="upload" className="!text-[18px]" />}
                                    Upload Backup
                                </Button>
                                <input
                                    ref={fileInputRef}
                                    type="file"
                                    accept=".zip,.sql,application/zip,text/plain"
                                    className="hidden"
                                    onChange={(e) => {
                                        const file = e.target.files?.[0];
                                        if (file) void uploadBackup(file);
                                    }}
                                />
                            </div>
                            <div
                                aria-live="polite"
                                className="min-w-0 whitespace-pre-line break-words font-mono text-xs text-base-content/70"
                            >
                                {backupProgress
                                    || (restoreProgress && restorePhase === "staging" ? restoreProgress : null)
                                    || (!wsConnected ? "Connecting for live progress…" : "Ready to create a backup.")}
                            </div>
                        </div>
                    </div>

                    {(message || listError) && (
                        <Alert
                            className="alert-soft text-sm"
                            variant={
                                listError || message?.variant === "danger"
                                    ? "danger"
                                    : message?.variant === "warning"
                                        ? "warning"
                                        : "success"
                            }
                        >
                            {listError ?? message?.text}
                        </Alert>
                    )}
                </div>
            </section>

            <section className="space-y-3">
                <div className="flex items-end justify-between gap-4">
                    <div>
                        <h2 className="text-lg font-semibold text-base-content">Backup library</h2>
                        <p className="mt-1 text-xs leading-relaxed text-base-content/50">
                            Download, preserve, restore, or delete existing snapshots.
                        </p>
                    </div>
                    <div className="flex items-center gap-2">
                        <span className="badge badge-ghost badge-sm shrink-0">
                            {backups.length} {backups.length === 1 ? "backup" : "backups"}
                        </span>
                        <Button variant="ghost" size="small" onClick={() => void refreshList()}>
                            <Icon name="refresh" className="!text-[16px]" />
                            Refresh
                        </Button>
                    </div>
                </div>

                {backups.length === 0 ? (
                    <div className="rounded-lg border border-dashed border-base-content/15 bg-base-200/20 px-4 py-8 text-center">
                        <Icon name="folder_off" className="!text-[28px] text-base-content/35" />
                        <p className="mt-2 text-sm text-base-content/55">No backups yet.</p>
                        <p className="mt-1 text-xs text-base-content/40">
                            Create one above or upload an archive to get started.
                        </p>
                    </div>
                ) : (
                    <ul className="space-y-3">
                        {backups.map((backup) => {
                            const totalBytes = backup.files.reduce((sum, f) => sum + (f.bytes || 0), 0);
                            return (
                                <li
                                    key={backup.id}
                                    className="overflow-hidden rounded-lg border border-base-content/10 bg-base-100"
                                >
                                    <div className="flex flex-wrap items-start justify-between gap-3 border-b border-base-content/10 p-4">
                                        <div className="min-w-0 space-y-1">
                                            <div className="flex flex-wrap items-center gap-2">
                                                <span className="font-mono text-sm text-base-content">{backup.id}</span>
                                                <Badge className="badge-sm badge-ghost">{backup.kind}</Badge>
                                                {backup.preserved && (
                                                    <Badge className="badge-sm badge-warning badge-soft">preserved</Badge>
                                                )}
                                            </div>
                                            <p className="text-[11px] text-base-content/50">
                                                {formatDate(backup.createdAt)} · {formatBytes(totalBytes)}
                                                {backup.appVersion ? ` · v${backup.appVersion}` : ""}
                                            </p>
                                        </div>
                                        <label className="flex cursor-pointer items-center gap-2 rounded-lg bg-base-200/40 px-3 py-1.5 text-xs text-base-content/80">
                                            <Checkbox
                                                className="checkbox-primary checkbox-sm"
                                                checked={backup.preserved}
                                                onChange={(e) =>
                                                    void updateBackup(backup.id, { preserved: e.target.checked })
                                                }
                                            />
                                            Preserve
                                        </label>
                                    </div>

                                    <div className="space-y-3 p-4">
                                        <Input
                                            defaultValue={backup.notes ?? ""}
                                            placeholder="Notes"
                                            onBlur={(e) => {
                                                if (e.target.value !== (backup.notes ?? "")) {
                                                    void updateBackup(backup.id, { notes: e.target.value });
                                                }
                                            }}
                                        />
                                        <div className="flex flex-wrap gap-2">
                                            <Button
                                                variant="outline"
                                                size="small"
                                                disabled={busy === `download-${backup.id}`}
                                                onClick={() => void downloadBackup(backup.id)}
                                            >
                                                {busy === `download-${backup.id}`
                                                    ? <Spinner className="h-4 w-4" />
                                                    : <Icon name="download" className="!text-[16px]" />}
                                                Download
                                            </Button>
                                            <Button
                                                variant="warning"
                                                size="small"
                                                disabled={!!busy || taskRunning}
                                                onClick={() => setConfirmRestoreId(backup.id)}
                                            >
                                                <Icon name="restore" className="!text-[16px]" />
                                                Restore
                                            </Button>
                                            <Button
                                                variant="danger"
                                                size="small"
                                                disabled={busy === `delete-${backup.id}`}
                                                onClick={() => setConfirmDeleteId(backup.id)}
                                            >
                                                <Icon name="delete" className="!text-[16px]" />
                                                Delete
                                            </Button>
                                        </div>
                                    </div>
                                </li>
                            );
                        })}
                    </ul>
                )}

                <div className="flex items-start gap-2 rounded-lg bg-base-200/30 px-3 py-2.5 text-xs leading-relaxed text-base-content/55">
                    <Icon name="info" className="mt-0.5 !text-[17px] shrink-0 text-base-content/45" />
                    <p>
                        {mainDatabaseProvider === "postgres"
                            ? <>Backups include only the local <code className="font-mono text-base-content/70">metrics.sqlite</code> and{" "}
                                <code className="font-mono text-base-content/70">warden.db</code> databases as logical SQL dumps. The
                                PostgreSQL main database is externally managed and is not included. </>
                            : <>Backups include <code className="font-mono text-base-content/70">db.sqlite</code>,{" "}
                                <code className="font-mono text-base-content/70">metrics.sqlite</code>, and{" "}
                                <code className="font-mono text-base-content/70">warden.db</code> as logical SQL dumps. </>}
                        <code className="font-mono text-base-content/70">blobs/</code> folder is not included — restoring
                        an older dump may leave some items with missing blob files.
                    </p>
                </div>
            </section>

            <ConfirmModal
                show={confirmDeleteId !== null}
                title="Delete backup"
                message={<>Delete backup <span className="font-mono">{confirmDeleteId}</span>? This cannot be undone.</>}
                cancelText="Cancel"
                confirmText="Delete"
                onCancel={() => setConfirmDeleteId(null)}
                onConfirm={() => {
                    if (confirmDeleteId) void deleteBackup(confirmDeleteId);
                }}
            />

            <ConfirmModal
                show={confirmRestoreId !== null}
                title="Restore database backup"
                message={
                    mainDatabaseProvider === "postgres"
                        ? <>
                            Restoring <span className="font-mono">{confirmRestoreId}</span> replaces only the local
                            metrics and warden SQLite databases. The externally managed PostgreSQL main database is not
                            changed. A pre-restore safety backup is created automatically.
                        </>
                        : <>
                            Restoring <span className="font-mono">{confirmRestoreId}</span> replaces all settings, queue,
                            history, and the WebDAV file tree. A pre-restore safety backup is created automatically. The
                            server will restart into maintenance mode to apply the swap.
                        </>
                }
                checkboxMessage={mainDatabaseProvider === "postgres"
                    ? "I understand this will replace the local SQLite databases"
                    : "I understand this will replace the current databases"}
                cancelText="Cancel"
                confirmText="Restore"
                onCancel={() => setConfirmRestoreId(null)}
                onConfirm={(checked) => {
                    if (confirmRestoreId) void startRestore(confirmRestoreId, checked);
                }}
            />
        </SettingsPage>
    );
}

export function isBackupSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return (
        config["backup.schedule-enabled"] !== newConfig["backup.schedule-enabled"] ||
        config["backup.schedule-time"] !== newConfig["backup.schedule-time"] ||
        config["backup.retention-count"] !== newConfig["backup.retention-count"]
    );
}

function getScheduledTime(config: Record<string, string>): { hour: number; minute: number; period: "am" | "pm" } {
    const totalMinutes = parseInt(config["backup.schedule-time"] || "0");
    const hour24 = Math.floor(totalMinutes / 60);
    return {
        hour: hour24 % 12 || 12,
        minute: totalMinutes % 60,
        period: Math.floor(totalMinutes / 60) >= 12 ? "pm" : "am",
    };
}

function buildScheduledTime(hour: number, minute: number, period: "am" | "pm"): string {
    const hour24 = (hour % 12) + (period === "pm" ? 12 : 0);
    return "" + (hour24 * 60 + minute);
}

function formatBytes(bytes: number): string {
    if (!Number.isFinite(bytes) || bytes <= 0) return "0 B";
    const units = ["B", "KB", "MB", "GB"];
    let value = bytes;
    let unit = 0;
    while (value >= 1024 && unit < units.length - 1) {
        value /= 1024;
        unit++;
    }
    return `${value.toFixed(unit === 0 ? 0 : 1)} ${units[unit]}`;
}

function formatDate(value: string): string {
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return value;
    return date.toLocaleString();
}
