import type { Route } from "./+types/route";
import { Breadcrumbs } from "./breadcrumbs/breadcrumbs";
import { Link, redirect, useLocation, useNavigation, useRevalidator } from "react-router";
import {
    backendClient,
    WebdavDirectoryNotFoundError,
    type DirectoryItem,
} from "~/clients/backend-client.server";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { lookup as getMimeType } from 'mime-types';
import { getDownloadKey } from "~/auth/downloads.server";
import { Loading } from "../_index/components/loading/loading";
import { formatFileSize } from "~/utils/file-size";
import { parseExploreWebdavPath } from "~/utils/path";
import { ItemMenu } from "./item-menu/item-menu";
import { ConfirmModal } from "~/components/confirm-modal/confirm-modal";
import { classNames } from "~/utils/styling";
import { Icon, Checkbox, Button } from "~/components/ui";
import { useIsReadOnly } from "~/auth/authorization";
import { withUrlBase } from "~/utils/url-base";

const ITEM_MENU_CLASS =
    "flex select-none items-center self-stretch rounded-r-lg px-5 py-[15px]";
const ITEM_MENU_OPEN_CLASS = "bg-base-content/10";

export type ExplorePageData = {
    parentDirectories: string[],
    items: (DirectoryItem | ExploreFile)[],
    error: "not-found" | null,
}

export type ExploreFile = DirectoryItem & {
    mimeType: string,
    downloadKey: string,
}

type SortKey = "name" | "size" | "type";
type SortDir = "asc" | "desc";
export async function loader({ request, params }: Route.LoaderArgs) {
    // if path ends in trailing slash, remove it
    if (request.url.endsWith('/')) return redirect(request.url.slice(0, -1));

    // Single-fetch navigation requests use an internal `.data` URL, so derive
    // the WebDAV path from the matched wildcard rather than request.url.
    const parsed = parseExploreWebdavPath(params["*"] ?? "");
    if (!parsed.ok) {
        return {
            parentDirectories: [],
            items: [],
            error: "not-found" as const,
        };
    }
    const path = parsed.path;
    try {
        return {
            parentDirectories: getParentDirectories(path),
            error: null,
            items: (await backendClient.listWebdavDirectory(path)).map(x => {
                if (x.isDirectory) return x;
                return {
                    ...x,
                    mimeType: getMimeType(x.name),
                    downloadKey: getDownloadKey(getRelativePath(path, x.name))
                };
            })
        };
    } catch (error) {
        if (!(error instanceof WebdavDirectoryNotFoundError)) throw error;
        return {
            parentDirectories: getParentDirectories(path),
            items: [],
            error: "not-found" as const,
        };
    }
}

export default function Explore({ loaderData }: Route.ComponentProps) {
    return (
        <Body {...loaderData} />
    );
}

function Body(props: ExplorePageData) {
    const isReadOnly = useIsReadOnly();
    const location = useLocation();
    const navigation = useNavigation();
    const revalidator = useRevalidator();
    const isNavigating = Boolean(navigation.location);

    const items = props.items;
    const parentDirectories = isNavigating
        ? getParentDirectories(getWebdavPathDecoded(navigation.location!.pathname))
        : props.parentDirectories;
    const canDelete = !isReadOnly && isDeletable(parentDirectories);

    const [query, setQuery] = useState("");
    const [sortKey, setSortKey] = useState<SortKey>("name");
    const [sortDir, setSortDir] = useState<SortDir>("asc");
    const [selected, setSelected] = useState<Set<string>>(() => new Set());
    const [pendingDelete, setPendingDelete] = useState<string[] | null>(null);
    const [deletePreview, setDeletePreview] = useState<DeletePreviewAggregate | null>(null);
    const [deleteError, setDeleteError] = useState<string | null>(null);
    const [isDeleting, setIsDeleting] = useState(false);
    const lastClickedRef = useRef<string | null>(null);

    // Reset selection and query when navigating between folders.
    useEffect(() => {
        setSelected(new Set());
        setQuery("");
        lastClickedRef.current = null;
    }, [location.pathname]);

    // Keep long-lived directory views in sync with queue and *Arr removals.
    useEffect(() => {
        const revalidateWhenVisible = () => {
            if (document.visibilityState === "visible" && revalidator.state === "idle") {
                revalidator.revalidate();
            }
        };

        const interval = window.setInterval(revalidateWhenVisible, 60_000);
        document.addEventListener("visibilitychange", revalidateWhenVisible);
        return () => {
            window.clearInterval(interval);
            document.removeEventListener("visibilitychange", revalidateWhenVisible);
        };
    }, [revalidator]);

    const visibleItems = useMemo(() => {
        const q = query.trim().toLowerCase();
        const filtered = q
            ? items.filter(x => x.name.toLowerCase().includes(q))
            : items.slice();
        filtered.sort((a, b) => compareItems(a, b, sortKey, sortDir));
        return filtered;
    }, [items, query, sortKey, sortDir]);

    const visibleNames = useMemo(() => visibleItems.map(i => i.name), [visibleItems]);
    const selectableNames = useMemo(
        () => visibleNames.filter(n => canDelete),
        [visibleNames, canDelete]
    );
    const selectedVisibleCount = useMemo(
        () => selectableNames.reduce((acc, n) => acc + (selected.has(n) ? 1 : 0), 0),
        [selectableNames, selected]
    );
    const allVisibleSelected = canDelete
        && selectableNames.length > 0
        && selectedVisibleCount === selectableNames.length;
    const someVisibleSelected = selectedVisibleCount > 0 && !allVisibleSelected;

    const stats = useMemo(() => {
        const dirs = visibleItems.filter(x => x.isDirectory).length;
        const files = visibleItems.length - dirs;
        const totalSize = visibleItems.reduce((acc, x) => acc + (x.size ?? 0), 0);
        return { dirs, files, totalSize };
    }, [visibleItems]);

    const getDirectoryPath = useCallback((directoryName: string) => {
        return `${location.pathname}/${encodeURIComponent(directoryName)}`;
    }, [location.pathname]);

    const getFilePath = useCallback((file: ExploreFile) => {
        const pathname = getWebdavPath(location.pathname);
        const relativePath = getRelativePath(pathname, encodeURIComponent(file.name));
        const extension = getExtension(file.name);
        const extensionQueryParam = extension ? `&extension=${extension}` : '';
        return `/view/${relativePath}?downloadKey=${file.downloadKey}${extensionQueryParam}`;
    }, [location.pathname]);

    const requestDelete = useCallback((names: string[]) => {
        if (names.length === 0) return;
        setDeleteError(null);
        setPendingDelete(names);
    }, []);

    const cancelDelete = useCallback(() => {
        if (isDeleting) return;
        setPendingDelete(null);
        setDeletePreview(null);
        setDeleteError(null);
    }, [isDeleting]);

    useEffect(() => {
        if (!pendingDelete || pendingDelete.length === 0) {
            setDeletePreview(null);
            return;
        }
        const pathname = getWebdavPathDecoded(location.pathname);
        let cancelled = false;
        (async () => {
            try {
                const previews: DeletePreviewResponse[] = [];
                for (const name of pendingDelete) {
                    if (cancelled) return;
                    const fullPath = pathname ? `${pathname}/${name}` : name;
                    const resp = await fetch(
                        withUrlBase(`/api/delete-webdav-item-preview?path=${encodeURIComponent(fullPath)}`),
                    );
                    if (!resp.ok) throw new Error("preview failed");
                    previews.push(await resp.json() as DeletePreviewResponse);
                }
                if (cancelled) return;
                setDeletePreview({
                    fileCount: previews.reduce((acc, p) => acc + p.fileCount, 0),
                    dirCount: previews.reduce((acc, p) => acc + p.dirCount, 0),
                    totalBytes: previews.reduce((acc, p) => acc + p.totalBytes, 0),
                    linkedHistoryCount: previews.reduce((acc, p) => acc + p.linkedHistoryCount, 0),
                });
            } catch {
                if (!cancelled) setDeletePreview(null);
            }
        })();
        return () => { cancelled = true; };
    }, [pendingDelete, location.pathname]);

    const performDelete = useCallback(async () => {
        if (!pendingDelete || pendingDelete.length === 0) return;
        const pathname = getWebdavPathDecoded(location.pathname);
        setIsDeleting(true);
        setDeleteError(null);
        const failures: string[] = [];
        for (const name of pendingDelete) {
            const fullPath = pathname ? `${pathname}/${name}` : name;
            const fd = new FormData();
            fd.append('path', fullPath);
            try {
                const resp = await fetch(withUrlBase('/api/delete-webdav-item'), { method: 'POST', body: fd });
                if (!resp.ok) {
                    const data = await resp.json().catch(() => ({} as any));
                    failures.push(`${name}: ${data.error || resp.statusText}`);
                }
            } catch (err: any) {
                failures.push(`${name}: ${err?.message || 'network error'}`);
            }
        }
        setIsDeleting(false);
        if (failures.length > 0) {
            setDeleteError(failures.join('\n'));
            return;
        }
        setSelected(prev => {
            const next = new Set(prev);
            for (const n of pendingDelete) next.delete(n);
            return next;
        });
        setPendingDelete(null);
        revalidator.revalidate();
    }, [pendingDelete, location.pathname, revalidator]);

    const toggleSelect = useCallback((name: string, shiftKey: boolean) => {
        if (!canDelete) return;
        setSelected(prev => {
            const next = new Set(prev);
            const last = lastClickedRef.current;
            if (shiftKey && last && last !== name) {
                const startIdx = selectableNames.indexOf(last);
                const endIdx = selectableNames.indexOf(name);
                if (startIdx !== -1 && endIdx !== -1) {
                    const [lo, hi] = startIdx < endIdx ? [startIdx, endIdx] : [endIdx, startIdx];
                    const shouldSelect = !prev.has(name);
                    for (let i = lo; i <= hi; i++) {
                        const n = selectableNames[i];
                        if (n === undefined) continue;
                        if (shouldSelect) next.add(n);
                        else next.delete(n);
                    }
                    lastClickedRef.current = name;
                    return next;
                }
            }
            if (next.has(name)) next.delete(name);
            else next.add(name);
            lastClickedRef.current = name;
            return next;
        });
    }, [canDelete, selectableNames]);

    const toggleSelectAll = useCallback(() => {
        if (!canDelete) return;
        setSelected(prev => {
            if (allVisibleSelected) {
                const next = new Set(prev);
                for (const n of selectableNames) next.delete(n);
                return next;
            }
            const next = new Set(prev);
            for (const n of selectableNames) next.add(n);
            return next;
        });
    }, [canDelete, allVisibleSelected, selectableNames]);

    const clearSelection = useCallback(() => setSelected(new Set()), []);

    // Keyboard shortcuts: Esc clears, Cmd/Ctrl+A selects all, Delete triggers bulk delete.
    useEffect(() => {
        const isTypingTarget = (el: EventTarget | null) => {
            if (!(el instanceof HTMLElement)) return false;
            const tag = el.tagName;
            return tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT" || el.isContentEditable;
        };
        const onKey = (e: KeyboardEvent) => {
            if (e.key === "Escape") {
                if (selected.size > 0) {
                    e.preventDefault();
                    clearSelection();
                }
                return;
            }
            if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === "a" && !isTypingTarget(e.target)) {
                if (!canDelete || selectableNames.length === 0) return;
                e.preventDefault();
                toggleSelectAll();
                return;
            }
            if ((e.key === "Delete" || e.key === "Backspace") && !isTypingTarget(e.target)) {
                if (selected.size === 0 || !canDelete) return;
                e.preventDefault();
                requestDelete(Array.from(selected));
            }
        };
        window.addEventListener("keydown", onKey);
        return () => window.removeEventListener("keydown", onKey);
    }, [selected, canDelete, selectableNames, toggleSelectAll, clearSelection, requestDelete]);

    const isRefreshing = revalidator.state === "loading";
    const showSkeleton = isNavigating;

    return (
        <div className="absolute flex min-h-full min-w-full flex-col px-4 py-4 text-base text-base-content/70 md:px-8">
            <Breadcrumbs parentDirectories={parentDirectories} />
            {!showSkeleton && props.error === "not-found" && (
                <div className="card bg-base-200 border-base-content/10 my-4 min-h-[320px] shadow-md">
                    <div className="card-body items-center justify-center text-center">
                        <Icon name="folder_off" className="!text-[48px] text-warning" />
                        <h2 className="card-title text-xl">Directory unavailable</h2>
                        <p className="text-base-content/60 max-w-md text-sm">
                            This WebDAV directory does not exist, may have moved, or is still initializing.
                        </p>
                        <div className="card-actions justify-center">
                                <Link to="/explore" discover="none" className="btn btn-primary btn-sm">
                                WebDAV root
                            </Link>
                        </div>
                    </div>
                </div>
            )}
            {props.error === null && <>
            <Toolbar
                query={query}
                onQueryChange={setQuery}
                sortKey={sortKey}
                sortDir={sortDir}
                onSortChange={(k, d) => { setSortKey(k); setSortDir(d); }}
                onRefresh={() => revalidator.revalidate()}
                isRefreshing={isRefreshing}
                stats={stats}
                totalCount={items.length}
                showingCount={visibleItems.length}
                isFiltered={query.trim().length > 0}
                canSelectAll={canDelete && selectableNames.length > 0}
                allSelected={allVisibleSelected}
                someSelected={someVisibleSelected}
                onToggleAll={toggleSelectAll}
            />
            {selected.size > 0 && canDelete && (
                <SelectionBar
                    count={selected.size}
                    onClear={clearSelection}
                    onDelete={() => requestDelete(Array.from(selected))}
                />
            )}
            {!showSkeleton && visibleItems.length === 0 && (
                <EmptyState
                    isFiltered={query.trim().length > 0}
                    onClearFilter={() => setQuery("")}
                />
            )}
            {!showSkeleton && visibleItems.length > 0 &&
                <div className="flex flex-col rounded-lg border border-base-content/10 divide-y divide-base-content/10">
                    {visibleItems.filter(x => x.isDirectory).map((x, index) => {
                        const checked = selected.has(x.name);
                        return (
                            <div key={`${index}_dir_item`} className={getClassName(x, checked)}>
                                {canDelete && (
                                    <CheckCell
                                        name={x.name}
                                        checked={checked}
                                        onToggle={toggleSelect}
                                    />
                                )}
                                <Link
                                    to={getDirectoryPath(x.name)}
                                    discover="none"
                                    className={getItemContentClassName(canDelete, canDelete)}
                                >
                                    <Icon name="folder" className="shrink-0 text-base-content/50 !text-[32px]" />
                                    <div className="break-all font-medium">{x.name}</div>
                                </Link>
                                {canDelete && (
                                    <ItemMenu
                                        className={ITEM_MENU_CLASS}
                                        openClassName={ITEM_MENU_OPEN_CLASS}
                                        onRemove={() => requestDelete([x.name])} />
                                )}
                            </div>
                        );
                    })}
                    {visibleItems.filter(x => !x.isDirectory).map((x, index) => {
                        const checked = selected.has(x.name);
                        return (
                            <div key={`${index}_file_item`} className={getClassName(x, checked)}>
                                {canDelete && (
                                    <CheckCell
                                        name={x.name}
                                        checked={checked}
                                        onToggle={toggleSelect}
                                    />
                                )}
                                <a
                                    href={getFilePath(x as ExploreFile)}
                                    className={getItemContentClassName(canDelete, true)}
                                >
                                    <Icon name={getIcon(x as ExploreFile)} className="text-base-content/50 shrink-0 !text-[34px]" />
                                    <div className="flex flex-col gap-1 leading-snug text-base-content">
                                        <div className="break-all font-medium">{x.name}</div>
                                        <div className="text-xs text-base-content/50">{formatFileSize(x.size)}</div>
                                    </div>
                                </a>
                                <ItemMenu
                                    className={ITEM_MENU_CLASS}
                                    openClassName={ITEM_MENU_OPEN_CLASS}
                                    exploreFile={x as ExploreFile}
                                    previewPath={getFilePath(x as ExploreFile)}
                                    {...(canDelete ? { onRemove: () => requestDelete([x.name]) } : {})} />
                            </div>
                        );
                    })}
                </div>
            }
            </>}
            {showSkeleton && <Loading className="w-[calc(100%-75px)] min-h-0 flex-1 grow" />}
            <ConfirmModal
                show={pendingDelete !== null}
                title={pendingDelete && pendingDelete.length > 1 ? "Delete items" : "Delete item"}
                message={renderDeleteMessage(pendingDelete, deletePreview)}
                confirmText={isDeleting ? "Deleting..." : "Delete"}
                cancelText="Cancel"
                {...(deleteError != null ? { errorMessage: deleteError } : {})}
                onCancel={cancelDelete}
                onConfirm={performDelete}
            />
        </div>
    );
}

type ToolbarProps = {
    query: string,
    onQueryChange: (q: string) => void,
    sortKey: SortKey,
    sortDir: SortDir,
    onSortChange: (key: SortKey, dir: SortDir) => void,
    onRefresh: () => void,
    isRefreshing: boolean,
    stats: { dirs: number, files: number, totalSize: number },
    totalCount: number,
    showingCount: number,
    isFiltered: boolean,
    canSelectAll: boolean,
    allSelected: boolean,
    someSelected: boolean,
    onToggleAll: () => void,
}

function Toolbar(props: ToolbarProps) {
    const sortValue = `${props.sortKey}:${props.sortDir}`;
    return (
        <div className="mb-4 flex flex-col gap-2">
            <div className="flex flex-row flex-wrap items-center gap-2.5">
                {props.canSelectAll && (
                    <label
                        className="border-base-content/10 bg-base-200 flex h-[38px] w-[38px] shrink-0 cursor-pointer items-center justify-center rounded-lg border"
                        title={props.allSelected ? "Clear selection" : "Select all"}
                    >
                        <Checkbox
                            checked={props.allSelected}
                            ref={el => { if (el) el.indeterminate = props.someSelected; }}
                            onChange={props.onToggleAll}
                            aria-label="Select all visible items"
                            className="checkbox-sm"
                        />
                    </label>
                )}
                <div className="relative min-w-[200px] flex-1">
                    <SearchIcon />
                    <input
                        type="search"
                        value={props.query}
                        onChange={e => props.onQueryChange(e.target.value)}
                        placeholder="Filter by name..."
                        className="input input-sm w-full pr-8 pl-9"
                        aria-label="Filter items"
                    />
                    {props.query && (
                        <button
                            type="button"
                            className="btn btn-ghost btn-xs btn-circle absolute top-1/2 right-1 -translate-y-1/2"
                            onClick={() => props.onQueryChange("")}
                            aria-label="Clear filter"
                        >
                            ×
                        </button>
                    )}
                </div>
                <select
                    className="select select-sm"
                    value={sortValue}
                    onChange={e => {
                        const [k, d] = e.target.value.split(":") as [SortKey, SortDir];
                        props.onSortChange(k, d);
                    }}
                    aria-label="Sort"
                >
                    <option value="name:asc">Name (A→Z)</option>
                    <option value="name:desc">Name (Z→A)</option>
                    <option value="size:desc">Size (largest)</option>
                    <option value="size:asc">Size (smallest)</option>
                    <option value="type:asc">Type</option>
                </select>
                <button
                    type="button"
                    className={classNames([
                        "btn btn-ghost btn-square btn-sm",
                        props.isRefreshing && "btn-disabled",
                    ])}
                    onClick={props.onRefresh}
                    disabled={props.isRefreshing}
                    aria-label="Refresh"
                    title="Refresh"
                >
                    {props.isRefreshing
                        ? <span className="loading loading-spinner loading-xs" />
                        : <RefreshIcon />}
                </button>
            </div>
            <div className="text-base-content/50 pl-0.5 text-xs">
                {props.isFiltered
                    ? `${props.showingCount} of ${props.totalCount} · `
                    : ""}
                {formatCount(props.stats.dirs, "folder")} · {formatCount(props.stats.files, "file")} · {formatFileSize(props.stats.totalSize)}
            </div>
        </div>
    );
}

function SelectionBar(props: { count: number, onClear: () => void, onDelete: () => void }) {
    return (
        <div className="alert bg-base-200 border-base-content/10 mb-3 flex-row items-center justify-between shadow-sm" role="region" aria-label="Bulk actions">
            <span className="text-sm font-medium">{props.count} selected</span>
            <div className="flex gap-2">
                <Button variant="ghost" size="small" onClick={props.onClear}>
                    Clear
                </Button>
                <Button variant="danger" size="small" onClick={props.onDelete}>
                    Delete {props.count}
                </Button>
            </div>
        </div>
    );
}

function EmptyState(props: { isFiltered: boolean, onClearFilter: () => void }) {
    if (props.isFiltered) {
        return (
            <div className="card bg-base-200 border-base-content/20 border-dashed shadow-none">
                <div className="card-body items-center text-center">
                    <h3 className="text-base font-medium">No matches</h3>
                    <p className="text-base-content/60 text-sm">Nothing in this folder matches your filter.</p>
                    <div className="card-actions justify-center">
                        <Button variant="primary" size="small" onClick={props.onClearFilter}>
                            <Icon name="filter_alt_off" className="!text-[18px]" />
                            Clear filter
                        </Button>
                    </div>
                </div>
            </div>
        );
    }
    return (
        <div className="card bg-base-200 border-base-content/20 border-dashed shadow-none">
            <div className="card-body items-center text-center">
                <h3 className="text-base font-medium">This folder is empty</h3>
                <p className="text-base-content/60 text-sm">Items downloaded into this folder will appear here.</p>
            </div>
        </div>
    );
}

function CheckCell(props: { name: string, checked: boolean, onToggle: (name: string, shiftKey: boolean) => void }) {
    return (
        <label
            className="flex shrink-0 cursor-pointer select-none items-center justify-center py-0 pr-1.5 pl-3.5"
            onClick={e => e.stopPropagation()}
        >
            <Checkbox
                checked={props.checked}
                onChange={e => props.onToggle(props.name, e.nativeEvent instanceof MouseEvent && e.nativeEvent.shiftKey)}
                onKeyDown={e => {
                    if (e.key === " " || e.key === "Enter") {
                        e.preventDefault();
                        e.stopPropagation();
                        props.onToggle(props.name, e.shiftKey);
                    }
                }}
                aria-label={`Select ${props.name}`}
                className="checkbox-sm"
            />
        </label>
    );
}

function renderDeleteMessage(
    pending: string[] | null,
    preview: DeletePreviewAggregate | null,
) {
    if (!pending || pending.length === 0) return null;
    const impact = preview
        ? (
            <div className="mt-2 text-base-content/70">
                Deletes {preview.fileCount} file{preview.fileCount === 1 ? "" : "s"} / {preview.dirCount} folder{preview.dirCount === 1 ? "" : "s"} ({formatFileSize(preview.totalBytes)}).
                {preview.linkedHistoryCount > 0 && (
                    <> Removes {preview.linkedHistoryCount} history entr{preview.linkedHistoryCount === 1 ? "y" : "ies"}.</>
                )}
            </div>
        )
        : null;
    if (pending.length === 1) {
        return (
            <div>
                Delete <strong>{pending[0]}</strong>?
                {impact}
                <div className="mt-2 text-base-content/50">This cannot be undone.</div>
            </div>
        );
    }
    return (
        <div>
            Delete <strong>{pending.length} items</strong>?
            <ul className="mt-2 max-h-40 overflow-y-auto pl-[18px]">
                {pending.slice(0, 30).map(n => <li key={n}>{n}</li>)}
                {pending.length > 30 && <li>…and {pending.length - 30} more</li>}
            </ul>
            {impact}
            <div className="mt-2 text-base-content/50">This cannot be undone.</div>
        </div>
    );
}

function compareItems(a: DirectoryItem, b: DirectoryItem, key: SortKey, dir: SortDir): number {
    // Directories always come before files, regardless of sort.
    if (a.isDirectory !== b.isDirectory) return a.isDirectory ? -1 : 1;
    const mult = dir === "asc" ? 1 : -1;
    if (key === "name") {
        return a.name.localeCompare(b.name, undefined, { numeric: true, sensitivity: "base" }) * mult;
    }
    if (key === "size") {
        const aSize = a.size ?? 0;
        const bSize = b.size ?? 0;
        if (aSize !== bSize) return (aSize - bSize) * mult;
        return a.name.localeCompare(b.name, undefined, { numeric: true, sensitivity: "base" });
    }
    // type
    const aKind = fileKindRank(a);
    const bKind = fileKindRank(b);
    if (aKind !== bKind) return (aKind - bKind) * mult;
    return a.name.localeCompare(b.name, undefined, { numeric: true, sensitivity: "base" });
}

function fileKindRank(item: DirectoryItem): number {
    const ext = getExtension(item.name)?.toLowerCase() ?? "";
    const mime = (item as ExploreFile).mimeType ?? "";
    if (mime.startsWith("video") || ext === ".mkv" || mime === "application/mp4") return 0;
    if (mime.startsWith("image")) return 1;
    if (mime.startsWith("audio")) return 2;
    return 3;
}

function formatCount(n: number, label: string) {
    return `${n} ${label}${n === 1 ? "" : "s"}`;
}

function getExtension(filename: string): string | undefined {
    const lastDotIndex = filename.lastIndexOf('.');
    if (lastDotIndex === -1 || lastDotIndex === 0) return undefined;
    return filename.slice(lastDotIndex);
}

function getIcon(file: ExploreFile) {
    if (file.name.toLowerCase().endsWith(".mkv")) return "movie";
    if (file.mimeType === "application/mp4") return "movie";
    if (file.mimeType && file.mimeType.startsWith("video")) return "movie";
    if (file.mimeType && file.mimeType.startsWith("image")) return "image";
    return "draft";
}

function getWebdavPath(pathname: string): string {
    if (pathname.startsWith("/")) pathname = pathname.slice(1);
    if (pathname.startsWith("explore")) pathname = pathname.slice(7);
    if (pathname.startsWith("/")) pathname = pathname.slice(1);
    return pathname;
}

function getWebdavPathDecoded(pathname: string): string {
    const parsed = parseExploreWebdavPath(getWebdavPath(pathname));
    return parsed.ok ? parsed.path : "";
}

function getRelativePath(path: string, filename: string) {
    if (path === "") return filename;
    return `${path}/${filename}`;
}

function getParentDirectories(webdavPath: string): string[] {
    return webdavPath == "" ? [] : webdavPath.split('/');
}

function isDeletable(parentDirectories: string[]): boolean {
    return parentDirectories[0] === "content" && parentDirectories.length >= 2;
}

type DeletePreviewResponse = {
    fileCount: number,
    dirCount: number,
    totalBytes: number,
    linkedHistoryCount: number,
};

type DeletePreviewAggregate = DeletePreviewResponse;

function getClassName(item: DirectoryItem | ExploreFile, isSelected: boolean) {
    return classNames([
        "relative flex bg-base-200 transition-colors duration-100",
        "first:rounded-t-lg last:rounded-b-lg",
        "has-[a:hover]:bg-base-100 has-[a:active]:bg-base-300",
        "[@media(hover:hover)]:has-[a:hover]:cursor-pointer",
        item.name.startsWith(".") && "opacity-50",
        isSelected && "bg-primary/10",
    ]);
}

function getItemContentClassName(hasCheckbox: boolean, hasMenu: boolean) {
    return classNames([
        "flex flex-1 items-center gap-3.5 px-4 py-3.5 text-inherit no-underline",
        hasCheckbox && "pl-0",
        hasMenu && "pr-1.5",
    ]);
}

function SearchIcon() {
    return (
        <svg className="text-base-content/50 pointer-events-none absolute top-1/2 left-3 -translate-y-1/2" xmlns="http://www.w3.org/2000/svg" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <circle cx="11" cy="11" r="7" />
            <path d="m21 21-4.3-4.3" />
        </svg>
    );
}

function RefreshIcon() {
    return (
        <svg xmlns="http://www.w3.org/2000/svg" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M3 12a9 9 0 0 1 15.5-6.4L21 8" />
            <path d="M21 3v5h-5" />
            <path d="M21 12a9 9 0 0 1-15.5 6.4L3 16" />
            <path d="M3 21v-5h5" />
        </svg>
    );
}
