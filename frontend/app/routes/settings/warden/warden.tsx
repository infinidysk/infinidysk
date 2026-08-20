import { type ChangeEvent, type DragEvent, type Dispatch, type SetStateAction, useEffect, useRef, useState } from "react";
import { ConfirmModal } from "~/components/confirm-modal/confirm-modal";
import { Alert, Button, Icon, ManagedSetting, Modal, NativeForm as Form, SettingsIntro, SettingsPage, Spinner, Textarea, Tooltip } from "~/components/ui";
import { withUrlBase } from "~/utils/url-base";

type WardenSettingsProps = {
    config: Record<string, string>;
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>;
};

type Trust = "full" | "corroborate" | "observe";

type Source = {
    id: string;
    kind: "local" | "remote" | "imported";
    name: string;
    url?: string | null;
    enabled: boolean;
    trust: Trust;
    refreshHours: number;
    lastChecked: number;
    lastUpdated: number;
    status?: string | null;
    count: number;
};

type Snapshot = {
    quorum: number;
    localCount: number;
    effectiveCount: number;
    totalRows: number;
    sources: Source[];
};

type Status = { text: string; variant: "success" | "danger" } | null;

type BackupStatus = {
    enabled: boolean;
    repo: string;
    path: string;
    branch: string;
    scope: "local" | "merged";
    intervalHours: number;
    hasToken: boolean;
    lastAt: number;
    lastStatus?: string | null;
    rawUrl?: string | null;
};

// Backend response contracts, mirroring backend/Api/Controllers/Warden/*.cs
type WardenImportResponse = {
    added: number;
    total: number;
    cleared: number;
    sourceId?: string | null;
};

type WardenSourceMutateResponse = {
    sourceId?: string | null;
    message?: string | null;
    removed: number;
};

type WardenSourcesImportResponse = {
    added: number;
    skipped: number;
    invalid: number;
};

type WardenBackupMutateResponse = {
    message: string;
};

const TRUST_HELP: Record<Trust, string> = {
    full: "Filters on its own",
    corroborate: "Filters only when enough sources agree",
    observe: "Never filters (watch only)",
};

function ago(sec?: number) {
    if (!sec) return "never";
    const d = Date.now() / 1000 - sec;
    if (d < 60) return "just now";
    if (d < 3600) return `${Math.floor(d / 60)}m ago`;
    if (d < 86400) return `${Math.floor(d / 3600)}h ago`;
    return `${Math.floor(d / 86400)}d ago`;
}

function kindMeta(kind: Source["kind"]) {
    if (kind === "local") return { label: "Local", cls: "text-success border-success/30 bg-success/10" };
    if (kind === "remote") return { label: "Remote", cls: "text-info border-info/30 bg-info/10" };
    return { label: "Imported", cls: "" };
}

function apiError(data: unknown) {
    return typeof data === "object" && data !== null && "error" in data && typeof data.error === "string"
        ? data.error
        : null;
}

function errorText(err: unknown, fallback: string) {
    return err instanceof Error && err.message ? err.message : fallback;
}

export function WardenSettings({ config, setNewConfig }: WardenSettingsProps) {
    const set = (key: string, value: string) => setNewConfig({ ...config, [key]: value });
    const hideDead = (config["warden.hide-dead"] ?? "true") === "true";
    const quorum = config["warden.quorum"] ?? "2";
    const backboneScope = (config["warden.backbone-scope"] ?? "true") === "true";

    const [snap, setSnap] = useState<Snapshot | null>(null);
    const [busy, setBusy] = useState<string | null>(null);
    const [message, setMessage] = useState<Status>(null);
    const [dragOver, setDragOver] = useState(false);
    const fileRef = useRef<HTMLInputElement>(null);

    const [showImport, setShowImport] = useState(false);
    const [importTarget, setImportTarget] = useState<"merge" | "separate">("separate");
    const [importName, setImportName] = useState("");
    const [importTrust, setImportTrust] = useState<Trust>("corroborate");
    const [pendingFile, setPendingFile] = useState<File | null>(null);

    const [showExport, setShowExport] = useState(false);
    const [exportScope, setExportScope] = useState<"local" | "merged">("local");
    const [exportSources, setExportSources] = useState<Set<string>>(new Set());
    const [exportDedup, setExportDedup] = useState(true);

    const [showAddRemote, setShowAddRemote] = useState(false);
    const [remoteUrl, setRemoteUrl] = useState("");
    const [remoteName, setRemoteName] = useState("");
    const [remoteInterval, setRemoteInterval] = useState("24");
    const [remoteTrust, setRemoteTrust] = useState<Trust>("corroborate");

    const [showBulk, setShowBulk] = useState(false);
    const [bulkText, setBulkText] = useState("");
    const [bulkTrust, setBulkTrust] = useState<Trust>("corroborate");
    const [bulkFile, setBulkFile] = useState<File | null>(null);
    const [bulkInterval, setBulkInterval] = useState("24");
    const bulkFileRef = useRef<HTMLInputElement>(null);

    const [confirm, setConfirm] = useState<{ kind: "remove" | "clear"; source: Source } | null>(null);

    const [backup, setBackup] = useState<BackupStatus | null>(null);
    const [showBackup, setShowBackup] = useState(false);
    const [confirmRestore, setConfirmRestore] = useState(false);
    const [bRepo, setBRepo] = useState("");
    const [bToken, setBToken] = useState("");
    const [bPath, setBPath] = useState("warden/warden.ndjson.gz");
    const [bBranch, setBBranch] = useState("main");
    const [bScope, setBScope] = useState<"local" | "merged">("local");
    const [bInterval, setBInterval] = useState("24");
    const [bEnabled, setBEnabled] = useState(false);

    const refresh = async () => {
        try {
            const res = await fetch(withUrlBase("/api/warden-sources"));
            // GET /api/warden-sources → WardenSourcesResponse
            if (res.ok) setSnap((await res.json()) as Snapshot);
        } catch { /* ignore */ }
    };

    const loadBackup = async () => {
        try {
            const res = await fetch(withUrlBase("/api/warden-backup"));
            // GET /api/warden-backup → WardenBackupStatusResponse
            if (res.ok) setBackup((await res.json()) as BackupStatus);
        } catch {
            // backup status stays at its default when the fetch fails
        }
    };

    // Initial load; both fetches catch internally and report via state.
    useEffect(() => { void refresh(); void loadBackup(); }, []);

    useEffect(() => {
        if (message?.variant !== "success") return;
        const t = setTimeout(() => setMessage(null), 4000);
        return () => clearTimeout(t);
    }, [message]);

    const post = async <T = unknown>(url: string, form: FormData): Promise<T> => {
        const res = await fetch(url, { method: "POST", body: form });
        const data: unknown = await res.json().catch(() => ({}));
        if (!res.ok) throw new Error(apiError(data) || "Request failed.");
        // The caller names the endpoint's response contract (see the Warden controllers).
        return data as T;
    };

    const submitImport = async () => {
        if (!pendingFile) return;
        setBusy("import");
        setMessage(null);
        try {
            const form = new FormData();
            form.append("file", pendingFile);
            form.append("target", importTarget);
            if (importTarget === "separate") {
                form.append("name", importName);
                form.append("trust", importTrust);
            }
            const data = await post<WardenImportResponse>("/api/warden-import", form);
            setMessage({ text: `Imported ${(data.added ?? 0).toLocaleString()} fingerprint${data.added === 1 ? "" : "s"}.`, variant: "success" });
            setShowImport(false);
            setPendingFile(null);
            setImportName("");
            await refresh();
        } catch (err: unknown) {
            setMessage({ text: errorText(err, "Import failed."), variant: "danger" });
        } finally {
            setBusy(null);
            if (fileRef.current) fileRef.current.value = "";
        }
    };

    const onFilePicked = (e: ChangeEvent<HTMLInputElement>) => {
        const file = e.target.files?.[0];
        if (file) setPendingFile(file);
    };

    const onDrop = (e: DragEvent<HTMLDivElement>) => {
        e.preventDefault();
        setDragOver(false);
        if (busy) return;
        const file = e.dataTransfer.files?.[0];
        if (file) {
            setPendingFile(file);
            setImportTarget("separate");
            setShowImport(true);
        }
    };

    const doExport = () => {
        const params = new URLSearchParams();
        if (exportScope === "merged") {
            params.set("scope", "merged");
            const ids = [...exportSources];
            if (ids.length) params.set("sources", ids.join(","));
        }
        params.set("dedup", exportDedup ? "1" : "0");
        const a = document.createElement("a");
        a.href = `/api/warden-export?${params.toString()}`;
        a.download = "warden.ndjson.gz";
        document.body.appendChild(a);
        a.click();
        a.remove();
        setShowExport(false);
    };

    const addRemoteSource = async () => {
        setBusy("add-remote");
        setMessage(null);
        try {
            const form = new FormData();
            form.append("url", remoteUrl.trim());
            form.append("name", remoteName.trim());
            form.append("trust", remoteTrust);
            form.append("refreshHours", remoteInterval);
            const data = await post<WardenSourceMutateResponse>("/api/warden-source-add", form);
            const failed = (data.message ?? "").startsWith("error");
            setMessage(failed
                ? { text: `Added, but the first fetch failed: ${data.message}`, variant: "danger" }
                : { text: `Remote list added (${data.message}).`, variant: "success" });
            setShowAddRemote(false);
            setRemoteUrl("");
            setRemoteName("");
            await refresh();
        } catch (err: unknown) {
            setMessage({ text: errorText(err, "Could not add the remote list."), variant: "danger" });
        } finally {
            setBusy(null);
        }
    };

    const submitBulk = async () => {
        setBusy("bulk");
        setMessage(null);
        try {
            const form = new FormData();
            if (bulkFile) form.append("file", bulkFile);
            if (bulkText.trim()) form.append("text", bulkText);
            form.append("trust", bulkTrust);
            form.append("refreshHours", bulkInterval);
            const data = await post<WardenSourcesImportResponse>("/api/warden-sources-import", form);
            const parts = [`Added ${(data.added ?? 0).toLocaleString()}`];
            if (data.skipped) parts.push(`${data.skipped.toLocaleString()} already present`);
            if (data.invalid) parts.push(`${data.invalid.toLocaleString()} invalid`);
            setMessage({ text: parts.join(" · ") + ".", variant: data.added ? "success" : "danger" });
            setShowBulk(false);
            setBulkText("");
            setBulkFile(null);
            if (bulkFileRef.current) bulkFileRef.current.value = "";
            await refresh();
        } catch (err: unknown) {
            setMessage({ text: errorText(err, "Import failed."), variant: "danger" });
        } finally {
            setBusy(null);
        }
    };

    const exportSourcesBundle = () => {
        const a = document.createElement("a");
        a.href = "/api/warden-sources-export";
        a.download = "bundle.json";
        document.body.appendChild(a);
        a.click();
        a.remove();
    };

    const updateSource = async (id: string, fields: Record<string, string>) => {
        setBusy("src-" + id);
        try {
            const form = new FormData();
            form.append("id", id);
            for (const [k, v] of Object.entries(fields)) form.append(k, v);
            await post("/api/warden-source-update", form);
            await refresh();
        } catch (err: unknown) {
            setMessage({ text: errorText(err, "Update failed."), variant: "danger" });
        } finally {
            setBusy(null);
        }
    };

    const refreshSource = async (id: string) => {
        setBusy("src-" + id);
        setMessage(null);
        try {
            const form = new FormData();
            form.append("id", id);
            const data = await post<WardenSourceMutateResponse>("/api/warden-source-refresh", form);
            const failed = (data.message ?? "").startsWith("error");
            setMessage({ text: `Refresh: ${data.message}`, variant: failed ? "danger" : "success" });
            await refresh();
        } catch (err: unknown) {
            setMessage({ text: errorText(err, "Refresh failed."), variant: "danger" });
        } finally {
            setBusy(null);
        }
    };

    const runConfirm = async () => {
        if (!confirm) return;
        const { kind, source } = confirm;
        setConfirm(null);
        setBusy("src-" + source.id);
        setMessage(null);
        try {
            const form = new FormData();
            form.append("id", source.id);
            if (kind === "clear") form.append("action", "clear");
            const data = await post<WardenSourceMutateResponse>("/api/warden-source-remove", form);
            setMessage({
                text: kind === "clear"
                    ? `Cleared ${(data.removed ?? 0).toLocaleString()} fingerprint${data.removed === 1 ? "" : "s"}.`
                    : `Removed “${source.name}”.`,
                variant: "success",
            });
            await refresh();
        } catch (err: unknown) {
            setMessage({ text: errorText(err, "Action failed."), variant: "danger" });
        } finally {
            setBusy(null);
        }
    };

    const openBackupModal = () => {
        setBRepo(backup?.repo ?? "");
        setBPath(backup?.path || "warden/warden.ndjson.gz");
        setBBranch(backup?.branch || "main");
        setBScope(backup?.scope === "merged" ? "merged" : "local");
        setBInterval(String(backup?.intervalHours ?? 24));
        setBEnabled(backup?.enabled ?? false);
        setBToken("");
        setShowBackup(true);
    };

    const saveBackup = async () => {
        setBusy("backup-save");
        setMessage(null);
        try {
            const form = new FormData();
            form.append("enabled", String(bEnabled));
            form.append("repo", bRepo.trim());
            form.append("path", bPath.trim());
            form.append("branch", bBranch.trim());
            form.append("scope", bScope);
            form.append("intervalHours", bInterval);
            if (bToken) form.append("token", bToken);
            const data = await post<BackupStatus>("/api/warden-backup", form);
            setBackup(data);
            setMessage({ text: "Backup settings saved.", variant: "success" });
            setShowBackup(false);
            setBToken("");
        } catch (err: unknown) {
            setMessage({ text: errorText(err, "Could not save backup settings."), variant: "danger" });
        } finally {
            setBusy(null);
        }
    };

    const backupNow = async () => {
        setBusy("backup-now");
        setMessage(null);
        try {
            const data = await post<WardenBackupMutateResponse>("/api/warden-backup-now", new FormData());
            const failed = (data.message ?? "").startsWith("error");
            setMessage({ text: `Backup: ${data.message}`, variant: failed ? "danger" : "success" });
            await loadBackup();
        } catch (err: unknown) {
            setMessage({ text: errorText(err, "Backup failed."), variant: "danger" });
        } finally {
            setBusy(null);
        }
    };

    const doRestore = async () => {
        setConfirmRestore(false);
        setBusy("backup-restore");
        setMessage(null);
        try {
            const data = await post<WardenBackupMutateResponse>("/api/warden-backup-restore", new FormData());
            setMessage({ text: data.message || "Restored.", variant: "success" });
            await refresh();
            await loadBackup();
        } catch (err: unknown) {
            setMessage({ text: errorText(err, "Restore failed."), variant: "danger" });
        } finally {
            setBusy(null);
        }
    };

    const sources = snap?.sources ?? [];

    return (
        <SettingsPage>
            <SettingsIntro>
                A portable filter list of dead-release fingerprints. Your own list fills in
                automatically and stays independent. You can also add remote lists from a URL;
                they refresh on their own schedule, never touch your own list, and you decide how
                much each one is trusted. Fingerprints are universal: identical on any provider,
                indexer, or server, and free of credentials.
            </SettingsIntro>

            <ManagedSetting configKey="warden.hide-dead">
            <Form.Group className={"flex flex-col gap-2"}>
                <Tooltip content="Remove search results whose fingerprint is filtered by your sources. If everything matches, results are shown anyway as a last resort.">
                    <Form.Check
                        type="switch"
                        id="warden-hide-dead"
                        className="cursor-pointer gap-2 p-0"
                        label="Filter out anything on the list"
                        checked={hideDead}
                        onChange={e => set("warden.hide-dead", String(e.target.checked))} />
                </Tooltip>
            </Form.Group>
            </ManagedSetting>

            <ManagedSetting configKey="warden.quorum">
            <Form.Group className={"flex flex-col gap-2"}>
                <Form.Label>Agreement needed for shared lists</Form.Label>
                <Form.Control className={"w-full max-w-md"} type="number" min={1} max={20}
                    value={quorum}
                    onChange={e => set("warden.quorum", String(Math.max(1, parseInt(e.target.value || "1", 10))))} />
                <p className={"m-0 text-[11px] leading-relaxed text-base-content/45"}>
                    A fingerprint from a “corroborate” source only filters when at least this many
                    independent sources agree. Your own list and “full”-trust sources always filter
                    on their own.
                </p>
            </Form.Group>
            </ManagedSetting>

            <ManagedSetting configKey="warden.backbone-scope">
            <Form.Group className={"flex flex-col gap-2"}>
                <Tooltip content="A remote/imported verdict only filters when its provider matches one of yours. Your own list always filters.">
                    <Form.Check
                        type="switch"
                        id="warden-backbone-scope"
                        className="cursor-pointer gap-2 p-0"
                        label="Only filter when the provider matches"
                        checked={backboneScope}
                        onChange={e => set("warden.backbone-scope", String(e.target.checked))} />
                </Tooltip>
            </Form.Group>
            </ManagedSetting>

            <div className={`${"flex flex-col gap-2"} ${"w-full"}`}>
                <div className={"flex items-start justify-between gap-3 mb-1"}>
                    <div>
                        <div className={"text-[0.95rem] font-semibold text-base-content"}>Sources</div>
                        <div className={"text-[0.8125rem] leading-relaxed text-base-content/55"}>
                            {snap === null
                                ? "Loading…"
                                : `Filtering now: ${snap.effectiveCount.toLocaleString()} · ${snap.totalRows.toLocaleString()} total across all sources`}
                        </div>
                    </div>
                    <div className={"flex shrink-0 flex-wrap gap-2"}>
                        <Button size="xsmall" onClick={() => setShowAddRemote(true)}>
                            <Icon name="add_link" className="!text-[16px]" />
                            Add remote backup
                        </Button>
                        <Button variant="primary" size="xsmall" disabled={busy !== null}
                            onClick={() => setShowBulk(true)}>
                            <Icon name="playlist_add" className="!text-[16px]" />
                            Bundle
                        </Button>
                        <Button variant="primary" size="xsmall" disabled={busy !== null}
                            onClick={() => { setPendingFile(null); setShowImport(true); }}>
                            <Icon name="upload" className="!text-[16px]" />
                            Import
                        </Button>
                        <Button variant="primary" size="xsmall" disabled={!snap || snap.totalRows === 0}
                            onClick={() => {
                                setExportScope("local");
                                setExportSources(new Set(sources.map(s => s.id)));
                                setShowExport(true);
                            }}>
                            <Icon name="download" className="!text-[16px]" />
                            Export
                        </Button>
                    </div>
                </div>

                <p className={"m-0 text-[11px] leading-relaxed text-base-content/45"}>
                    Each source has a trust level. <b>full</b> filters on its own;{" "}
                    <b>corroborate</b> filters only when enough sources agree (the number above);{" "}
                    <b>observe</b> keeps the list but never filters.
                </p>

                <div
                    className={`${"flex flex-col gap-2.5 rounded-lg transition-shadow duration-120"} ${dragOver ? "ring-2 ring-primary/40" : ""}`}
                    onDragOver={e => { e.preventDefault(); if (!busy) setDragOver(true); }}
                    onDragLeave={e => { e.preventDefault(); setDragOver(false); }}
                    onDrop={onDrop}>
                    {sources.map(s => {
                        const isLocal = s.kind === "local";
                        const rowBusy = busy === "src-" + s.id;
                        const statusErr = (s.status ?? "").startsWith("error");
                        const km = kindMeta(s.kind);
                        return (
                            <div key={s.id} className={`${"rounded-lg border border-base-content/10 bg-base-100 p-3.5 transition-[border-color,opacity] duration-120 hover:border-base-content/20"} ${!s.enabled ? "opacity-55" : ""}`}>
                                <div className={"flex items-center gap-2.5"}>
                                    <div className={"flex min-w-0 flex-1 items-center gap-2.5"}>
                                        {isLocal
                                            ? <span className={"truncate text-sm font-semibold text-base-content"}>{s.name}</span>
                                            : <input className={"min-w-0 max-w-[240px] rounded-md border border-transparent bg-transparent px-1.5 py-0.5 text-sm font-semibold text-base-content hover:border-base-content/10 focus:border-base-content/10 focus:bg-base-200 focus:outline-none"} defaultValue={s.name} disabled={rowBusy}
                                                key={`nm-${s.id}-${s.name}`}
                                                onBlur={e => { const v = e.target.value.trim(); if (v && v !== s.name) void updateSource(s.id, { name: v }); /* failures surface via setMessage */ }}
                                                onKeyDown={e => { if (e.key === "Enter") e.currentTarget.blur(); }} />}
                                        <span className={`${"shrink-0 rounded-md border border-base-content/10 bg-base-200 px-2 py-0.5 text-[9px] font-semibold uppercase tracking-wider text-base-content/80"} ${km.cls}`}>{km.label}</span>
                                        {rowBusy && <Spinner size="sm" />}
                                    </div>
                                    <div className={"ml-auto flex shrink-0 items-center gap-1.5"}>
                                        {s.kind === "remote" &&
                                            <Button variant="primary" size="xsmall" disabled={rowBusy} onClick={() => void refreshSource(s.id)}>
                                                <Icon name="refresh" className="!text-[16px]" />
                                                Refresh
                                            </Button>}
                                        <Button variant="warning" size="xsmall" disabled={rowBusy || s.count === 0}
                                            onClick={() => setConfirm({ kind: "clear", source: s })}>
                                            <Icon name="delete_sweep" className="!text-[16px]" />
                                            Clear
                                        </Button>
                                        {!isLocal &&
                                            <Button variant="danger" size="xsmall" disabled={rowBusy}
                                                onClick={() => setConfirm({ kind: "remove", source: s })}>
                                                <Icon name="delete" className="!text-[16px]" />
                                                Remove
                                            </Button>}
                                    </div>
                                </div>

                                {s.url && <div className={"mt-1.5 break-all font-mono text-[11px] text-base-content/60"}>{s.url}</div>}

                                <div className={"mt-3 flex flex-wrap items-center gap-4 border-t border-base-content/10 pt-3"}>
                                    <span className={"text-xs text-base-content/80"}>{s.count.toLocaleString()} fingerprints</span>
                                    {isLocal
                                        ? <span className={"text-xs text-base-content/80"}>Trust: full · always on</span>
                                        : <div className={"flex items-center gap-1.5"}>
                                            <span className={"text-xs text-base-content/80"}>Trust</span>
                                            <Form.Select className={"w-[123px] shrink-0 text-xs"} value={s.trust} disabled={rowBusy}
                                                onChange={e => void updateSource(s.id, { trust: e.target.value })}>
                                                <option value="full">full</option>
                                                <option value="corroborate">corroborate</option>
                                                <option value="observe">observe</option>
                                            </Form.Select>
                                            <span className={"text-xs text-base-content/80"}>{TRUST_HELP[s.trust]}</span>
                                        </div>}

                                    {!isLocal &&
                                        <Tooltip content="Include fingerprints from this remote source in filtering.">
                                            <Form.Check type="switch" id={`enabled-${s.id}`} label="Enabled"
                                                className="cursor-pointer gap-2 p-0"
                                                checked={s.enabled} disabled={rowBusy}
                                                onChange={e => void updateSource(s.id, { enabled: String(e.target.checked) })} />
                                        </Tooltip>}

                                    {s.kind === "remote" &&
                                        <div className={"flex items-center gap-1.5"}>
                                            <span className={"text-xs text-base-content/80"}>Refresh (h)</span>
                                            <Form.Control type="number" min={1} max={720} className={"w-[72px] shrink-0 text-xs"}
                                                key={`rh-${s.id}-${s.refreshHours}`} defaultValue={s.refreshHours} disabled={rowBusy}
                                                onBlur={e => { const v = parseInt(e.target.value, 10); if (v && v !== s.refreshHours) void updateSource(s.id, { refreshHours: String(v) }); /* failures surface via setMessage */ }}
                                                onKeyDown={e => { if (e.key === "Enter") e.currentTarget.blur(); }} />
                                        </div>}

                                    {s.kind === "remote" &&
                                        <span className={`${"ml-auto text-[11px] text-base-content/60"} ${statusErr ? "text-error" : ""}`}>
                                            updated {ago(s.lastUpdated)}{s.status ? ` · ${s.status}` : ""}
                                        </span>}
                                </div>
                            </div>
                        );
                    })}
                </div>

                <p className={"mt-2 mb-0 text-[0.8rem] text-base-content/60"}>
                    {dragOver ? "Drop to import" : "Drag & drop a warden file here to import."}
                </p>
            </div>

            <div className={`${"flex flex-col gap-2"} ${"w-full"}`}>
                <div className={"flex items-start justify-between gap-3 mb-1"}>
                    <div>
                        <div className={"text-[0.95rem] font-semibold text-base-content"}>Backup to GitHub</div>
                        <div className={"text-[0.8125rem] leading-relaxed text-base-content/55"}>
                            {backup === null
                                ? "Loading…"
                                : backup.repo
                                    ? `${backup.enabled ? `Auto every ${backup.intervalHours}h` : "Manual only"} · ${backup.repo} · last backup ${ago(backup.lastAt)}${backup.lastStatus ? ` · ${backup.lastStatus}` : ""}`
                                    : "Not configured. Push your list to your own GitHub repo as a backup."}
                        </div>
                    </div>
                    <div className={"flex shrink-0 flex-wrap gap-2"}>
                        <Button size="xsmall" onClick={openBackupModal}>
                            <Icon name="settings" className="!text-[16px]" />
                            Settings
                        </Button>
                        <Button variant="primary" size="xsmall" disabled={!backup?.hasToken || busy !== null}
                            onClick={() => void backupNow()}>
                            {busy === "backup-now"
                                ? <><Spinner size="sm" /> Backing up…</>
                                : <><Icon name="cloud_upload" className="!text-[16px]" /> Back up now</>}
                        </Button>
                        <Button variant="warning" size="xsmall" disabled={!backup?.hasToken || busy !== null}
                            onClick={() => setConfirmRestore(true)}>
                            <Icon name="cloud_download" className="!text-[16px]" />
                            Restore
                        </Button>
                    </div>
                </div>

                <p className={"m-0 text-[11px] leading-relaxed text-base-content/45"}>
                    A personal backup of your fingerprint list to a repo you own. Keep the repo{" "}
                    <b>private</b> for a pure backup, or public to share. The file holds only fingerprint
                    hashes, no credentials. Restore reuses the same repo and token, so private repos work too.
                </p>

                {backup?.repo && backup.rawUrl &&
                    <div className={"mt-1.5 break-all font-mono text-[11px] text-base-content/60"}>{backup.rawUrl}</div>}
            </div>

            {message &&
                <Alert variant={message.variant} className={`${"max-w-[760px] m-0"} flex items-center justify-between gap-3`}>
                    <span>{message.text}</span>
                    <button type="button" className="rounded p-1 hover:bg-white/10" onClick={() => setMessage(null)} aria-label="Dismiss message">
                        <span className="material-symbols-rounded !text-[16px]" aria-hidden="true">close</span>
                    </button>
                </Alert>}

            <input ref={fileRef} type="file" accept=".gz,.ndjson,.json,application/gzip,application/json"
                style={{ display: "none" }} onChange={onFilePicked} />

            <Modal
                open={showAddRemote}
                title="Add a remote backup"
                onClose={() => setShowAddRemote(false)}
                footer={<>
                    <Button variant="outline" onClick={() => setShowAddRemote(false)}>Cancel</Button>
                    <Button disabled={busy === "add-remote" || !remoteUrl.trim()} onClick={() => void addRemoteSource()}>
                        {busy === "add-remote" ? <><Spinner size="sm" /> Adding…</> : "Add & fetch"}
                    </Button>
                </>}
            >
                    <Form.Group className={"mb-3.5"}>
                        <Form.Label>Backup URL</Form.Label>
                        <Form.Control type="url" placeholder="https://raw.githubusercontent.com/…/backup.ndjson.gz"
                            value={remoteUrl} onChange={e => setRemoteUrl(e.target.value)} />
                        <Form.Text muted>A raw .ndjson or .ndjson.gz file. GitHub raw URLs work great.</Form.Text>
                    </Form.Group>
                    <Form.Group className={"mb-3.5"}>
                        <Form.Label>Name (optional)</Form.Label>
                        <Form.Control value={remoteName} onChange={e => setRemoteName(e.target.value)} placeholder="Shared list" />
                    </Form.Group>
                    <div className={"flex gap-3"}>
                        <Form.Group style={{ flex: 1 }}>
                            <Form.Label>Trust</Form.Label>
                            <Form.Select value={remoteTrust} onChange={e => setRemoteTrust(e.target.value as Trust)}>
                                <option value="corroborate">corroborate (recommended)</option>
                                <option value="full">full</option>
                                <option value="observe">observe</option>
                            </Form.Select>
                            <Form.Text muted>{TRUST_HELP[remoteTrust]}</Form.Text>
                        </Form.Group>
                        <Form.Group style={{ width: 130 }}>
                            <Form.Label>Refresh (h)</Form.Label>
                            <Form.Control type="number" min={1} max={720} value={remoteInterval}
                                onChange={e => setRemoteInterval(e.target.value)} />
                        </Form.Group>
                    </div>
            </Modal>

            <Modal
                open={showBulk}
                title="Bundle"
                onClose={() => setShowBulk(false)}
                footer={<>
                    <Button variant="outline" onClick={() => setShowBulk(false)}>Cancel</Button>
                    <Button disabled={busy === "bulk" || (!bulkText.trim() && !bulkFile)} onClick={() => void submitBulk()}>
                        {busy === "bulk" ? <><Spinner size="sm" /> Importing…</> : "Import"}
                    </Button>
                </>}
            >
                    <Form.Group className={"mb-3.5"}>
                        <Form.Label>Paste entries (one per line)</Form.Label>
                        <Textarea rows={5} value={bulkText}
                            onChange={e => setBulkText(e.target.value)}
                            placeholder={"https://example.com/a.ndjson.gz\nhttps://example.com/b.ndjson.gz"} />
                        <Form.Text muted>Or choose a file below. Lines starting with # are ignored. Ones you already have are skipped.</Form.Text>
                    </Form.Group>
                    <div className={"flex items-center gap-2.5"}>
                        <Button variant="primary" size="xsmall" onClick={() => bulkFileRef.current?.click()}>
                            <Icon name="attach_file" className="!text-[16px]" />
                            Choose file…
                        </Button>
                        <span className={"text-[13px] text-base-content/80"}>{bulkFile ? bulkFile.name : "No file selected"}</span>
                    </div>
                    <input ref={bulkFileRef} type="file" accept=".json,.txt,application/json,text/plain"
                        style={{ display: "none" }} onChange={e => setBulkFile(e.target.files?.[0] ?? null)} />
                    <div className={"flex gap-3"} style={{ marginTop: 14 }}>
                        <Form.Group style={{ flex: 1 }}>
                            <Form.Label>Trust for these</Form.Label>
                            <Form.Select value={bulkTrust} onChange={e => setBulkTrust(e.target.value as Trust)}>
                                <option value="corroborate">corroborate (recommended)</option>
                                <option value="full">full</option>
                                <option value="observe">observe</option>
                            </Form.Select>
                            <Form.Text muted>{TRUST_HELP[bulkTrust]}. A file can override per entry.</Form.Text>
                        </Form.Group>
                        <Form.Group style={{ width: 130 }}>
                            <Form.Label>Refresh (h)</Form.Label>
                            <Form.Control type="number" min={1} max={720} value={bulkInterval}
                                onChange={e => setBulkInterval(e.target.value)} />
                            <Form.Text muted>Per entry in a file wins.</Form.Text>
                        </Form.Group>
                    </div>
                    <hr />
                    <div className={"flex items-center gap-2.5"}>
                        <Button variant="primary" size="xsmall" disabled={!sources.some(s => s.kind === "remote")}
                            onClick={exportSourcesBundle}>
                            <Icon name="download" className="!text-[16px]" />
                            Export bundle…
                        </Button>
                        <span className={"text-[13px] text-base-content/80"}>Save a file you can share or re-import.</span>
                    </div>
            </Modal>

            <Modal
                open={showImport}
                title="Import a warden file"
                onClose={() => setShowImport(false)}
                footer={<>
                    <Button variant="outline" onClick={() => setShowImport(false)}>Cancel</Button>
                    <Button disabled={busy === "import" || !pendingFile} onClick={() => void submitImport()}>
                        {busy === "import" ? <><Spinner size="sm" /> Importing…</> : "Import"}
                    </Button>
                </>}
            >
                    <Form.Check type="radio" name="import-target" id="import-separate" label="Keep as a separate source (recommended)"
                        checked={importTarget === "separate"} onChange={() => setImportTarget("separate")} />
                    <div className={"mb-3 ml-[26px] mt-0.5 text-[13px] text-base-content/60"}>
                        Stays isolated and reversible: one click to remove later. Best for lists from other people.
                    </div>
                    <Form.Check type="radio" name="import-target" id="import-merge" label="Merge into my list"
                        checked={importTarget === "merge"} onChange={() => setImportTarget("merge")} />
                    <div className={"mb-3 ml-[26px] mt-0.5 text-[13px] text-base-content/60"}>
                        Folds the fingerprints into your own list. Can’t be un-merged.
                    </div>

                    {importTarget === "separate" &&
                        <div className={"flex gap-3"} style={{ marginBottom: 14 }}>
                            <Form.Group style={{ flex: 1 }}>
                                <Form.Label>Name</Form.Label>
                                <Form.Control value={importName} onChange={e => setImportName(e.target.value)} placeholder="Imported list" />
                            </Form.Group>
                            <Form.Group style={{ width: 180 }}>
                                <Form.Label>Trust</Form.Label>
                                <Form.Select value={importTrust} onChange={e => setImportTrust(e.target.value as Trust)}>
                                    <option value="corroborate">corroborate</option>
                                    <option value="full">full</option>
                                    <option value="observe">observe</option>
                                </Form.Select>
                                <Form.Text muted>{TRUST_HELP[importTrust]}</Form.Text>
                            </Form.Group>
                        </div>}

                    <div className={"flex items-center gap-2.5"}>
                        <Button variant="primary" size="xsmall" onClick={() => fileRef.current?.click()}>
                            <Icon name="attach_file" className="!text-[16px]" />
                            Choose file…
                        </Button>
                        <span className={"text-[13px] text-base-content/80"}>{pendingFile ? pendingFile.name : "No file selected"}</span>
                    </div>
            </Modal>

            <Modal
                open={showExport}
                title="Export"
                onClose={() => setShowExport(false)}
                footer={<>
                    <Button variant="outline" onClick={() => setShowExport(false)}>Cancel</Button>
                    <Button disabled={exportScope === "merged" && exportSources.size === 0} onClick={doExport}>
                        Download
                    </Button>
                </>}
            >
                    <Form.Check type="radio" name="export-scope" id="export-local" label="My list only"
                        checked={exportScope === "local"} onChange={() => setExportScope("local")} />
                    <div className={"mb-3 ml-[26px] mt-0.5 text-[13px] text-base-content/60"}>
                        Just your own verdicts: the clean file others can trust. Publish it and share the URL.
                    </div>
                    <Form.Check type="radio" name="export-scope" id="export-merged" label="Merged from selected sources"
                        checked={exportScope === "merged"} onChange={() => setExportScope("merged")} />
                    {exportScope === "merged" &&
                        <div style={{ margin: "8px 0 12px 26px" }}>
                            {sources.map(s =>
                                <Form.Check key={s.id} type="checkbox" id={`exp-${s.id}`} label={`${s.name} (${s.count.toLocaleString()})`}
                                    checked={exportSources.has(s.id)}
                                    onChange={e => {
                                        const next = new Set(exportSources);
                                        if (e.target.checked) next.add(s.id); else next.delete(s.id);
                                        setExportSources(next);
                                    }} />)}
                        </div>}
                    <Tooltip content="Collapse identical fingerprints into a single export line.">
                        <Form.Check type="switch" id="export-dedup" label="Deduplicate identical fingerprints"
                            className="cursor-pointer gap-2 p-0"
                            checked={exportDedup} onChange={e => setExportDedup(e.target.checked)} style={{ marginTop: 8 }} />
                    </Tooltip>
            </Modal>

            <Modal
                open={showBackup}
                title="Backup to GitHub"
                onClose={() => setShowBackup(false)}
                footer={<>
                    <Button variant="outline" onClick={() => setShowBackup(false)}>Cancel</Button>
                    <Button disabled={busy === "backup-save" || !bRepo.trim()} onClick={() => void saveBackup()}>
                        {busy === "backup-save" ? <><Spinner size="sm" /> Saving…</> : "Save"}
                    </Button>
                </>}
            >
                    <Form.Group className={"mb-3.5"}>
                        <Form.Label>Repository</Form.Label>
                        <Form.Control placeholder="owner/repo" value={bRepo} onChange={e => setBRepo(e.target.value)} />
                        <Form.Text muted>A repo you own. Private is recommended for a personal backup.</Form.Text>
                    </Form.Group>
                    <Form.Group className={"mb-3.5"}>
                        <Form.Label>GitHub token</Form.Label>
                        <Form.Control type="password" autoComplete="new-password"
                            placeholder={backup?.hasToken ? "•••••••• (stored, leave blank to keep)" : "Fine-grained PAT"}
                            value={bToken} onChange={e => setBToken(e.target.value)} />
                        <Form.Text muted>
                            Fine-grained PAT scoped to this one repo, Contents: read and write. Stored
                            write-only, never shown again or returned by the API.
                        </Form.Text>
                    </Form.Group>
                    <div className={"flex gap-3"}>
                        <Form.Group style={{ flex: 1 }}>
                            <Form.Label>File path</Form.Label>
                            <Form.Control value={bPath} onChange={e => setBPath(e.target.value)} placeholder="warden/warden.ndjson.gz" />
                        </Form.Group>
                        <Form.Group style={{ width: 130 }}>
                            <Form.Label>Branch</Form.Label>
                            <Form.Control value={bBranch} onChange={e => setBBranch(e.target.value)} placeholder="main" />
                        </Form.Group>
                    </div>
                    <div className={"flex gap-3"}>
                        <Form.Group style={{ flex: 1 }}>
                            <Form.Label>What to back up</Form.Label>
                            <Form.Select value={bScope} onChange={e => setBScope(e.target.value as "local" | "merged")}>
                                <option value="local">My list only</option>
                                <option value="merged">Everything (all sources)</option>
                            </Form.Select>
                        </Form.Group>
                        <Form.Group style={{ width: 130 }}>
                            <Form.Label>Every (h)</Form.Label>
                            <Form.Control type="number" min={1} max={720} value={bInterval}
                                onChange={e => setBInterval(e.target.value)} />
                        </Form.Group>
                    </div>
                    <Tooltip content="Push an automatic backup of your list to GitHub on the schedule below.">
                        <Form.Check type="switch" id="backup-enabled" label="Back up automatically on this schedule"
                            className="cursor-pointer gap-2 p-0"
                            checked={bEnabled} onChange={e => setBEnabled(e.target.checked)} style={{ marginTop: 12 }} />
                    </Tooltip>
            </Modal>

            <ConfirmModal
                show={confirmRestore}
                title="Restore from backup?"
                message="This replaces your own list with the backup pulled from GitHub. Remote and imported sources are not affected."
                cancelText="Cancel"
                confirmText="Restore"
                onCancel={() => setConfirmRestore(false)}
                onConfirm={() => void doRestore()} />

            <ConfirmModal
                show={confirm !== null}
                title={confirm?.kind === "remove" ? "Remove this source?" : "Clear this source?"}
                message={confirm?.kind === "remove"
                    ? `This removes “${confirm?.source.name}” and its ${(confirm?.source.count ?? 0).toLocaleString()} fingerprints. You can add it again later.`
                    : `This empties “${confirm?.source.name}” (${(confirm?.source.count ?? 0).toLocaleString()} fingerprints).${confirm?.source.kind === "local" ? " Your list will repopulate automatically over time." : ""}`}
                cancelText="Cancel"
                confirmText={confirm?.kind === "remove" ? "Remove" : "Clear"}
                onCancel={() => setConfirm(null)}
                onConfirm={() => void runConfirm()} />
        </SettingsPage>
    );
}

export function isWardenSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["warden.hide-dead"] !== newConfig["warden.hide-dead"]
        || config["warden.quorum"] !== newConfig["warden.quorum"]
        || config["warden.backbone-scope"] !== newConfig["warden.backbone-scope"];
}
