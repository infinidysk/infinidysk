import type { Route } from "./+types/route";
import { useCallback, useEffect, useRef, useState } from "react";
import { useFetcher, useSearchParams, useLocation } from "react-router";
import { ConfirmModal } from "~/components/confirm-modal/confirm-modal";
import { Alert, Badge, Button, Icon, NativeForm as Form } from "~/components/ui";
import { Checkbox } from "~/components/ui/form";
import { backendClient, type WatchtowerData, type WatchtowerItem, type WatchtowerSource } from "~/clients/backend-client.server";
import { useIsReadOnly } from "~/auth/authorization";

const POLL_INTERVAL_MS = 5000;
const PAGE_SIZE = 100;

const SCOPE_OPTIONS: { value: string; label: string }[] = [
    { value: "", label: "Default scope" },
    { value: "latest-season", label: "Latest season" },
    { value: "first-season", label: "First season" },
    { value: "all-aired", label: "All aired seasons" },
    { value: "recent", label: "Recent episodes" },
    { value: "off", label: "Don't expand" },
];

export async function loader({ request }: Route.LoaderArgs) {
    const sp = new URL(request.url).searchParams;
    return await backendClient.getWatchtower({
        state: sp.get("state") ?? undefined,
        q: sp.get("q")?.trim() || undefined,
        sort: sp.get("sort") ?? undefined,
        offset: Number(sp.get("offset")) || 0,
        limit: Number(sp.get("limit")) || PAGE_SIZE,
        expander: sp.get("expander") ?? undefined,
        statsOnly: sp.get("statsOnly") === "1",
    });
}

export async function action({ request }: Route.ActionArgs) {
    const form = await request.formData();
    const fields: Record<string, string> = {};
    for (const [k, v] of form.entries()) fields[k] = typeof v === "string" ? v : v.name;
    try {
        if (fields["action"] === "discover-catalogs") {
            const discovered = await backendClient.discoverStremioCatalogs(fields["url"] ?? "");
            return { ok: true as const, discovered };
        }
        if (fields["action"] === "bulk-recheck" || fields["action"] === "bulk-remove") {
            const sub = fields["action"] === "bulk-recheck" ? "recheck-items" : "remove-items";
            await backendClient.watchtowerMutate({ action: sub, keys: fields["keys"] ?? "" });
            return { ok: true as const };
        }
        await backendClient.watchtowerMutate(fields);
        return { ok: true as const };
    } catch (e: unknown) {
        return { ok: false as const, error: e instanceof Error ? e.message : String(e) };
    }
}

type PendingRemove = "bulk" | "filter";

export default function Watchtower({ loaderData }: Route.ComponentProps) {
    const isReadOnly = useIsReadOnly();
    const addFetcher = useFetcher<typeof action>();
    const discoverFetcher = useFetcher<typeof action>();
    const bulkFetcher = useFetcher<typeof action>();
    const bulkItemFetcher = useFetcher<typeof action>();
    const filterFetcher = useFetcher<typeof action>();
    const moreFetcher = useFetcher<WatchtowerData>();
    const statsFetcher = useFetcher<WatchtowerData>();
    const childFetcher = useFetcher<WatchtowerData>();
    const location = useLocation();
    const [searchParams, setSearchParams] = useSearchParams();

    const stateFilter = searchParams.get("state");
    const urlQuery = searchParams.get("q") ?? "";
    const sortKey = searchParams.get("sort") ?? "default";
    const filtering = stateFilter !== null || urlQuery !== "";

    const discovered = discoverFetcher.data?.ok && "discovered" in discoverFetcher.data
        ? discoverFetcher.data.discovered
        : undefined;
    const discoverError = discoverFetcher.data && discoverFetcher.data.ok === false
        ? discoverFetcher.data.error : undefined;

    const [selected, setSelected] = useState<Set<string>>(new Set());
    const [discoveryDismissed, setDiscoveryDismissed] = useState(false);
    const [pendingRemove, setPendingRemove] = useState<PendingRemove | null>(null);

    const [items, setItems] = useState<WatchtowerItem[]>(loaderData.items);
    const [shows, setShows] = useState<WatchtowerItem[]>(loaderData.shows);
    const [total, setTotal] = useState(loaderData.total);
    const [hasMore, setHasMore] = useState(loaderData.hasMore);
    const [stats, setStats] = useState(loaderData.stats);
    const [sources, setSources] = useState(loaderData.sources);
    const [enabled, setEnabled] = useState(loaderData.enabled);

    const [selectedItems, setSelectedItems] = useState<Set<string>>(new Set());
    const [expandedShows, setExpandedShows] = useState<Set<string>>(new Set());
    const [childLoaded, setChildLoaded] = useState<Set<string>>(new Set());
    const selectAllRef = useRef<HTMLInputElement>(null);
    const sentinelRef = useRef<HTMLDivElement>(null);
    const lastOffsetRef = useRef(-1);
    const pendingChildRef = useRef<string | null>(null);
    const childLoadedRef = useRef<Set<string>>(new Set());

    const toggleItem = (key: string) => setSelectedItems(prev => {
        const next = new Set(prev);
        if (next.has(key)) next.delete(key); else next.add(key);
        return next;
    });
    const toggleShow = (key: string) => setExpandedShows(prev => {
        const next = new Set(prev);
        if (next.has(key)) next.delete(key); else next.add(key);
        return next;
    });

    useEffect(() => {
        setItems(loaderData.items);
        setShows(loaderData.shows);
        setTotal(loaderData.total);
        setHasMore(loaderData.hasMore);
        setStats(loaderData.stats);
        setSources(loaderData.sources);
        setEnabled(loaderData.enabled);
        setSelectedItems(new Set());
        setChildLoaded(new Set());
        lastOffsetRef.current = -1;
        pendingChildRef.current = null;
        childLoadedRef.current = new Set();
    }, [loaderData]);

    useEffect(() => {
        if (moreFetcher.state === "idle" && moreFetcher.data) {
            const data = moreFetcher.data;
            setItems(prev => {
                const seen = new Set(prev.map(i => i.key));
                return [...prev, ...data.items.filter(i => !seen.has(i.key))];
            });
            setTotal(data.total);
            setHasMore(data.hasMore);
        }
    }, [moreFetcher.state, moreFetcher.data]);

    useEffect(() => {
        if (childFetcher.state === "idle" && childFetcher.data && pendingChildRef.current) {
            const key = pendingChildRef.current;
            pendingChildRef.current = null;
            const incoming = childFetcher.data.items;
            setItems(prev => {
                const seen = new Set(prev.map(i => i.key));
                return [...prev, ...incoming.filter(i => !seen.has(i.key))];
            });
            const next = new Set(childLoadedRef.current);
            next.add(key);
            childLoadedRef.current = next;
            setChildLoaded(next);
        }
    }, [childFetcher.state, childFetcher.data]);

    useEffect(() => {
        if (childFetcher.state !== "idle" || pendingChildRef.current) return;
        const keys = new Set(shows.map(s => s.key));
        const open = urlQuery !== "" ? shows.map(s => s.key) : [...expandedShows];
        const next = open.find(k => keys.has(k) && !childLoadedRef.current.has(k));
        if (!next) return;
        pendingChildRef.current = next;
        const sp = new URLSearchParams();
        if (stateFilter) sp.set("state", stateFilter);
        if (urlQuery) sp.set("q", urlQuery);
        sp.set("expander", next);
        void childFetcher.load(`${location.pathname}?${sp.toString()}`); // fire-and-forget: result handled via fetcher state
    }, [shows, expandedShows, childLoaded, childFetcher.state, stateFilter, urlQuery, location.pathname]);

    useEffect(() => {
        if (statsFetcher.state === "idle" && statsFetcher.data) {
            setStats(statsFetcher.data.stats);
            setSources(statsFetcher.data.sources);
            setEnabled(statsFetcher.data.enabled);
        }
    }, [statsFetcher.state, statsFetcher.data]);

    const loadMoreRef = useRef<() => void>(() => {});
    loadMoreRef.current = () => {
        if (!hasMore || moreFetcher.state !== "idle") return;
        const offset = items.length;
        if (offset === lastOffsetRef.current) return;
        lastOffsetRef.current = offset;
        const sp = new URLSearchParams();
        if (stateFilter) sp.set("state", stateFilter);
        if (urlQuery) sp.set("q", urlQuery);
        if (sortKey !== "default") sp.set("sort", sortKey);
        sp.set("offset", String(offset));
        void moreFetcher.load(`${location.pathname}?${sp.toString()}`); // fire-and-forget: result handled via fetcher state
    };
    const pollRef = useRef<() => void>(() => {});
    pollRef.current = () => {
        if (statsFetcher.state === "idle") void statsFetcher.load(`${location.pathname}?statsOnly=1`); // fire-and-forget poll
    };

    useEffect(() => {
        const el = sentinelRef.current;
        if (!el) return;
        const io = new IntersectionObserver(es => { if (es[0].isIntersecting) loadMoreRef.current(); }, { rootMargin: "600px" });
        io.observe(el);
        return () => io.disconnect();
    }, []);

    useEffect(() => {
        if (moreFetcher.state !== "idle" || !hasMore) return;
        const el = sentinelRef.current;
        if (el && el.getBoundingClientRect().top < window.innerHeight) loadMoreRef.current();
    }, [items.length, hasMore, moreFetcher.state]);

    useEffect(() => {
        const t = setInterval(() => pollRef.current(), POLL_INTERVAL_MS);
        return () => clearInterval(t);
    }, []);

    const [queryInput, setQueryInput] = useState(urlQuery);
    useEffect(() => { setQueryInput(urlQuery); }, [urlQuery]);
    useEffect(() => {
        if (queryInput.trim() === urlQuery) return;
        const t = setTimeout(() => {
            setSearchParams(prev => {
                const next = new URLSearchParams(prev);
                const v = queryInput.trim();
                if (v) next.set("q", v); else next.delete("q");
                next.delete("offset");
                return next;
            }, { preventScrollReset: true });
        }, 300);
        return () => clearTimeout(t);
    }, [queryInput, urlQuery, setSearchParams]);

    const updateParams = (mut: (p: URLSearchParams) => void) => setSearchParams(prev => {
        const next = new URLSearchParams(prev);
        mut(next);
        next.delete("offset");
        return next;
    }, { preventScrollReset: true });
    const toggleState = (s: string) => updateParams(p => (stateFilter === s ? p.delete("state") : p.set("state", s)));
    const setSortKey = (s: string) => updateParams(p => (s && s !== "default" ? p.set("sort", s) : p.delete("sort")));
    const clearFilters = () => updateParams(p => { p.delete("state"); p.delete("q"); });

    useEffect(() => {
        if (discovered) {
            setSelected(new Set(discovered.catalogs.map(c => c.url)));
            setDiscoveryDismissed(false);
        }
    }, [discovered]);

    useEffect(() => {
        if (bulkFetcher.state === "idle" && bulkFetcher.data?.ok) {
            setDiscoveryDismissed(true);
        }
    }, [bulkFetcher.state, bulkFetcher.data]);

    const chosenCatalogs = (discovered?.catalogs ?? []).filter(c => selected.has(c.url));
    const sourcesJson = JSON.stringify(chosenCatalogs.map(c => ({
        url: c.url,
        name: discovered?.addonName ? `${discovered.addonName}: ${c.name}` : c.name,
    })));

    const expanders = shows;
    const expanderKeys = new Set(expanders.map(ex => ex.key));
    const childrenByExpander = new Map<string, WatchtowerItem[]>();
    for (const it of items) {
        if (!it.expanderKey || !expanderKeys.has(it.expanderKey)) continue;
        const arr = childrenByExpander.get(it.expanderKey);
        if (arr) arr.push(it); else childrenByExpander.set(it.expanderKey, [it]);
    }
    const orphans = items.filter(it =>
        it.state !== "expander" && (!it.expanderKey || !expanderKeys.has(it.expanderKey)));

    const forceOpenShows = urlQuery !== "";
    const isShowOpen = (key: string) => forceOpenShows || expandedShows.has(key);

    const allVisibleLeafKeys = [
        ...orphans.map(it => it.key),
        ...expanders.flatMap(ex => (childrenByExpander.get(ex.key) ?? []).map(k => k.key)),
    ];
    const allVisibleSelected = allVisibleLeafKeys.length > 0 && allVisibleLeafKeys.every(k => selectedItems.has(k));
    const someVisibleSelected = allVisibleLeafKeys.some(k => selectedItems.has(k));
    const toggleSelectAllVisible = () => setSelectedItems(prev => {
        const next = new Set(prev);
        if (allVisibleSelected) allVisibleLeafKeys.forEach(k => next.delete(k));
        else allVisibleLeafKeys.forEach(k => next.add(k));
        return next;
    });
    const setKeysSelected = (keys: string[], select: boolean) => setSelectedItems(prev => {
        const next = new Set(prev);
        if (select) keys.forEach(k => next.add(k)); else keys.forEach(k => next.delete(k));
        return next;
    });
    const bulkKeysValue = [...selectedItems].join("\n");

    const bulkBusy = bulkItemFetcher.state !== "idle";
    const filterBusy = filterFetcher.state !== "idle";
    const nothingShown = items.length === 0 && shows.length === 0;

    useEffect(() => {
        if (selectAllRef.current) selectAllRef.current.indeterminate = someVisibleSelected && !allVisibleSelected;
    }, [someVisibleSelected, allVisibleSelected]);

    const handleConfirmRemove = useCallback(() => {
        if (pendingRemove === "bulk") {
            // fire-and-forget: result handled via fetcher state
            void bulkItemFetcher.submit(
                { action: "bulk-remove", keys: bulkKeysValue },
                { method: "post" },
            );
        } else if (pendingRemove === "filter") {
            const formData: Record<string, string> = { action: "remove-by-filter" };
            if (stateFilter) formData["state"] = stateFilter;
            if (urlQuery) formData["q"] = urlQuery;
            void filterFetcher.submit(formData, { method: "post" }); // fire-and-forget: result handled via fetcher state
        }
        setPendingRemove(null);
    }, [pendingRemove, bulkKeysValue, bulkItemFetcher, filterFetcher, stateFilter, urlQuery]);

    const removeConfirmMessage = pendingRemove === "filter"
        ? `Remove all ${total} matching item${total === 1 ? "" : "s"} from Watchtower?`
        : `Remove ${selectedItems.size} item${selectedItems.size === 1 ? "" : "s"} from Watchtower?`;

    return (
        <div className={`mx-auto flex w-full max-w-[1200px] flex-col gap-6 px-4 py-4 text-sm text-base-content/70 md:px-8 ${isReadOnly ? "[&_form]:hidden" : ""}`}>
            <div className="flex flex-wrap items-start justify-between gap-4">
                <div>
                    <h2 className="text-xl font-semibold tracking-tight text-base-content">Watchtower</h2>
                    <p className="mt-1.5 max-w-[660px] text-xs leading-relaxed text-base-content/50">
                        Keeps your lists ready. Each title is pre-resolved to a healthy release and
                        re-verified over time, so it's found and ready before you need it. Pointer-only:
                        it stores segment maps, never video.
                    </p>
                </div>
                <div className="stats stats-vertical w-full border border-base-content/10 shadow sm:stats-horizontal">
                    <StatButton label="Ready" value={stats.ready} tone="ok" active={stateFilter === "ready"} onClick={() => toggleState("ready")} />
                    <StatButton label="Scouting" value={stats.scouting} tone="warn" active={stateFilter === "scouting"} onClick={() => toggleState("scouting")} />
                    <StatButton label="Unavailable" value={stats.unavailable} tone="bad" active={stateFilter === "unavailable"} onClick={() => toggleState("unavailable")} />
                    {stats.parked > 0 && (
                        <StatButton label="Parked" value={stats.parked} active={stateFilter === "parked"} onClick={() => toggleState("parked")} />
                    )}
                    <StatButton label="Shows" value={stats.expanders} active={stateFilter === "expander"} onClick={() => toggleState("expander")} />
                    <StatButton label="Total" value={stats.total} active={stateFilter === null} onClick={() => clearFilters()} />
                </div>
            </div>

            {!enabled && (
                <Alert variant="warning" className="text-xs">
                    Watchtower is off. Enable it under Settings, Watchtower to start readying these items.
                    You can still add lists and items now.
                </Alert>
            )}

            {addFetcher.data && addFetcher.data.ok === false && (
                <Alert variant="danger" className="text-xs">Action failed: {addFetcher.data.error}</Alert>
            )}

            <section className="card border border-base-content/10 bg-base-100 shadow-sm">
                <div className="card-body gap-4 p-4 md:p-5">
                    <div className="flex flex-col gap-1">
                        <h3 className="text-sm font-semibold text-base-content">Lists</h3>
                        <p className="text-xs leading-relaxed text-base-content/50">
                            Any list that yields content ids: a Stremio catalog URL, a plain list URL, or
                            manual additions. They merge into one deduped wanted-set.
                        </p>
                    </div>

                    {sources.length === 0
                        ? <p className="text-xs text-base-content/50">No lists yet. Add one below.</p>
                        : <div className="divide-y divide-base-content/10 rounded-lg border border-base-content/10">
                            {sources.map(s => <SourceRow key={s.id} source={s} />)}
                          </div>}

                    <addFetcher.Form method="post" className="flex flex-wrap items-center gap-2">
                        <input type="hidden" name="action" value="add-source" />
                        <Form.Select name="kind" defaultValue="stremio-catalog" className="select-sm max-w-[170px]">
                            <option value="stremio-catalog">Stremio catalog</option>
                            <option value="url-list">URL list</option>
                        </Form.Select>
                        <Form.Control name="name" placeholder="Name (optional)" className="input-sm max-w-[170px]" />
                        <Form.Control name="url" placeholder="https://addon/catalog/movie/xyz.json" className="input-sm min-w-[220px] flex-1" />
                        <Form.Control name="cap" type="number" min={0} placeholder="cap" className="input-sm max-w-[100px]" title="Per-list active cap (0 = use default)" />
                        <Form.Select name="seriesScope" defaultValue="" className="select-sm max-w-[170px]" title="Series scope for this list">
                            {SCOPE_OPTIONS.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
                        </Form.Select>
                        <Button type="submit" variant="primary" disabled={addFetcher.state !== "idle"}>Add list</Button>
                    </addFetcher.Form>

                    <div className="flex flex-col gap-3 border-t border-dashed border-base-content/10 pt-4">
                        <p className="text-xs leading-relaxed text-base-content/50">
                            Or paste a Stremio addon's <code className="rounded border border-base-content/10 bg-base-200 px-1 font-mono text-[11px]">manifest.json</code> URL to see its catalogs and pick the ones you want.
                            Each catalog you add becomes its own list.
                        </p>
                        <discoverFetcher.Form method="post" className="flex flex-wrap items-center gap-2">
                            <input type="hidden" name="action" value="discover-catalogs" />
                            <Form.Control
                                name="url"
                                placeholder="https://addon.example.com/.../manifest.json"
                                className="input-sm min-w-[220px] flex-1"
                            />
                            <Button type="submit" variant="primary" disabled={discoverFetcher.state !== "idle"}>
                                <Icon name={discoverFetcher.state !== "idle" ? "progress_activity" : "travel_explore"} className={`!text-[18px] ${discoverFetcher.state !== "idle" ? "animate-spin" : ""}`} />
                                {discoverFetcher.state !== "idle" ? "Loading…" : "Discover catalogs"}
                            </Button>
                        </discoverFetcher.Form>

                        {discoverError && <Alert variant="danger" className="text-xs">{discoverError}</Alert>}

                        {discovered && !discoveryDismissed && (
                            <div className="card border border-base-content/10 bg-base-200/40 shadow-sm">
                                <div className="card-body gap-3 p-4">
                                    <div className="flex flex-wrap items-center justify-between gap-3">
                                        <div className="text-xs font-semibold text-base-content">
                                            {discovered.addonName ? `${discovered.addonName} · ` : ""}
                                            {discovered.catalogs.length} catalog{discovered.catalogs.length === 1 ? "" : "s"} found
                                        </div>
                                        <div className="join">
                                            <button type="button" className="btn btn-ghost btn-xs join-item"
                                                onClick={() => setSelected(new Set(discovered.catalogs.map(c => c.url)))}>select all</button>
                                            <button type="button" className="btn btn-ghost btn-xs join-item"
                                                onClick={() => setSelected(new Set())}>select none</button>
                                            <button type="button" className="btn btn-ghost btn-xs join-item"
                                                onClick={() => setDiscoveryDismissed(true)}>close</button>
                                        </div>
                                    </div>

                                    <div className="max-h-[340px] overflow-y-auto divide-y divide-base-content/10">
                                        {discovered.catalogs.map(cat => (
                                            <label key={cat.url} className="flex min-w-0 cursor-pointer items-center gap-2.5 py-2">
                                                <Checkbox
                                                    checked={selected.has(cat.url)}
                                                    onChange={(e) => setSelected(prev => {
                                                        const next = new Set(prev);
                                                        if (e.target.checked) next.add(cat.url); else next.delete(cat.url);
                                                        return next;
                                                    })}
                                                />
                                                <Badge className="badge-ghost badge-sm uppercase">{cat.type}</Badge>
                                                <span className="shrink-0 font-medium text-base-content">{cat.name}</span>
                                                {cat.extraRequired && (
                                                    <Badge className="badge-warning badge-sm"
                                                        title={`This catalog requires "${cat.extraRequired}"; the basic endpoint may return nothing.`}>
                                                        needs {cat.extraRequired}
                                                    </Badge>
                                                )}
                                                <span className="min-w-0 flex-1 truncate text-right font-mono text-[11px] text-base-content/50" title={cat.url}>{cat.url}</span>
                                            </label>
                                        ))}
                                    </div>

                                    <div className="flex flex-wrap items-center gap-3">
                                        <bulkFetcher.Form method="post" className="flex flex-wrap items-center gap-2">
                                            <input type="hidden" name="action" value="add-sources" />
                                            <input type="hidden" name="sources" value={sourcesJson} readOnly />
                                            <Form.Select name="seriesScope" defaultValue="" className="select-sm max-w-[170px]" title="Series scope for these lists">
                                                {SCOPE_OPTIONS.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
                                            </Form.Select>
                                            <Button type="submit" variant="primary"
                                                disabled={bulkFetcher.state !== "idle" || selected.size === 0}>
                                                {bulkFetcher.state !== "idle" ? "Adding…" : `Add ${selected.size} selected`}
                                            </Button>
                                        </bulkFetcher.Form>
                                        {bulkFetcher.data && bulkFetcher.data.ok === false && (
                                            <span className="text-xs text-error">{bulkFetcher.data.error}</span>
                                        )}
                                    </div>
                                </div>
                            </div>
                        )}
                    </div>
                </div>
            </section>

            <section className="card border border-base-content/10 bg-base-100 shadow-sm">
                <div className="card-body gap-4 p-4 md:p-5">
                    <div className="flex flex-col gap-1">
                        <h3 className="text-sm font-semibold text-base-content">Wanted</h3>
                        <p className="text-xs leading-relaxed text-base-content/50">
                            Each item is searched once, the biggest healthy release is verified, then
                            re-checked over time. Add one manually by imdb id, or let your lists fill it.
                        </p>
                    </div>

                    <addFetcher.Form method="post" className="flex flex-wrap items-center gap-2">
                        <input type="hidden" name="action" value="add-item" />
                        <Form.Select name="type" defaultValue="movie" className="select-sm max-w-[170px]">
                            <option value="movie">movie</option>
                            <option value="series">series</option>
                        </Form.Select>
                        <Form.Control name="id" placeholder="tt0111161  (or tt0903747:1:2 for an episode)" className="input-sm min-w-[220px] flex-1" />
                        <Form.Control name="title" placeholder="Title (optional)" className="input-sm max-w-[170px]" />
                        <Button type="submit" variant="primary" disabled={addFetcher.state !== "idle"}>Add item</Button>
                    </addFetcher.Form>

                    {stats.total > 0 && (
                        <div className="flex flex-wrap items-center gap-2">
                            <label className="label cursor-pointer gap-1.5 p-0 text-xs text-base-content/70" title="Select all items shown">
                                <Checkbox
                                    ref={selectAllRef}
                                    checked={allVisibleSelected}
                                    onChange={toggleSelectAllVisible}
                                    disabled={allVisibleLeafKeys.length === 0}
                                    className="checkbox-sm"
                                />
                                All
                            </label>
                            <Form.Control
                                value={queryInput}
                                onChange={e => setQueryInput(e.target.value)}
                                placeholder="Search title or id…"
                                className="input-sm min-w-[200px] max-w-xs flex-1"
                            />
                            <Form.Select
                                value={sortKey}
                                onChange={e => setSortKey(e.target.value)}
                                className="select-sm max-w-[200px]"
                                title="Sort wanted items"
                            >
                                <option value="default">Sort: recent</option>
                                <option value="status">Status (issues first)</option>
                                <option value="title">Title A–Z</option>
                                <option value="recheck">Re-check soonest</option>
                            </Form.Select>
                            {filtering && (
                                <button type="button" className="btn btn-ghost btn-xs" onClick={clearFilters}>clear</button>
                            )}
                            {stats.unavailable > 0 && (
                                <filterFetcher.Form method="post" className="ml-auto flex items-center">
                                    <input type="hidden" name="action" value="recheck-by-filter" />
                                    <input type="hidden" name="state" value="unavailable" />
                                    <button type="submit" className="btn btn-ghost btn-xs" disabled={filterBusy}>
                                        re-check {stats.unavailable} unavailable
                                    </button>
                                </filterFetcher.Form>
                            )}
                        </div>
                    )}

                    {filtering && total > 0 && (
                        <div className="alert alert-soft flex min-h-0 flex-wrap items-center gap-2 py-2 text-xs">
                            <Badge className="badge-sm font-mono font-bold tabular-nums">{Math.min(items.length, total)}</Badge>
                            <span className="text-base-content/70">of {total} shown</span>
                            <div className="ml-auto flex flex-wrap items-center gap-2">
                                <filterFetcher.Form method="post">
                                    <input type="hidden" name="action" value="recheck-by-filter" />
                                    {stateFilter && <input type="hidden" name="state" value={stateFilter} />}
                                    {urlQuery && <input type="hidden" name="q" value={urlQuery} />}
                                    <Button type="submit" size="xsmall" variant="primary" disabled={filterBusy}>
                                        <Icon name={filterBusy ? "progress_activity" : "refresh"} className={`!text-[16px] ${filterBusy ? "animate-spin" : ""}`} />
                                        {filterBusy ? "Working…" : `Re-check all ${total}`}
                                    </Button>
                                </filterFetcher.Form>
                                <Button
                                    type="button"
                                    size="xsmall"
                                    variant="danger"
                                    disabled={filterBusy || isReadOnly}
                                    onClick={() => setPendingRemove("filter")}
                                >
                                    <Icon name="delete" className="!text-[16px]" />
                                    Remove all {total}
                                </Button>
                            </div>
                        </div>
                    )}

                    {selectedItems.size > 0 && (
                        <div className="alert alert-soft flex min-h-0 flex-wrap items-center gap-2 py-2 text-xs">
                            <Badge className="badge-sm font-mono font-bold tabular-nums">{selectedItems.size}</Badge>
                            <span className="text-base-content/70">selected</span>
                            <div className="ml-auto flex flex-wrap items-center gap-2">
                                <bulkItemFetcher.Form method="post">
                                    <input type="hidden" name="action" value="bulk-recheck" />
                                    <input type="hidden" name="keys" value={bulkKeysValue} readOnly />
                                    <Button type="submit" size="xsmall" variant="primary" disabled={bulkBusy}>
                                        <Icon name={bulkBusy ? "progress_activity" : "refresh"} className={`!text-[16px] ${bulkBusy ? "animate-spin" : ""}`} />
                                        {bulkBusy ? "Working…" : "Re-check"}
                                    </Button>
                                </bulkItemFetcher.Form>
                                <Button
                                    type="button"
                                    size="xsmall"
                                    variant="danger"
                                    disabled={bulkBusy || isReadOnly}
                                    onClick={() => setPendingRemove("bulk")}
                                >
                                    <Icon name="delete" className="!text-[16px]" />
                                    Remove
                                </Button>
                                <button type="button" className="btn btn-ghost btn-xs" onClick={() => setSelectedItems(new Set())}>Clear</button>
                            </div>
                        </div>
                    )}

                    {(bulkItemFetcher.data && bulkItemFetcher.data.ok === false) && (
                        <Alert variant="danger" className="text-xs">Bulk action failed: {bulkItemFetcher.data.error}</Alert>
                    )}
                    {(filterFetcher.data && filterFetcher.data.ok === false) && (
                        <Alert variant="danger" className="text-xs">Bulk action failed: {filterFetcher.data.error}</Alert>
                    )}

                    {nothingShown
                        ? <p className="text-xs text-base-content/50">{filtering ? "No items match." : "Nothing wanted yet."}</p>
                        : <div className="divide-y divide-base-content/10 rounded-lg border border-base-content/10">
                            {expanders.map(ex => (
                                <ExpanderGroup
                                    key={ex.key}
                                    expander={ex}
                                    episodes={childrenByExpander.get(ex.key) ?? []}
                                    expanded={isShowOpen(ex.key)}
                                    childrenLoaded={childLoaded.has(ex.key)}
                                    canToggle={!forceOpenShows}
                                    onToggle={() => toggleShow(ex.key)}
                                    selectedKeys={selectedItems}
                                    onToggleSelect={toggleItem}
                                    onSelectMany={setKeysSelected}
                                />
                            ))}
                            {orphans.map(it => (
                                <ItemRow
                                    key={it.key}
                                    item={it}
                                    selected={selectedItems.has(it.key)}
                                    onToggleSelect={toggleItem}
                                />
                            ))}
                          </div>}

                    <div ref={sentinelRef} />
                    {moreFetcher.state !== "idle" && <p className="text-xs text-base-content/50">Loading more…</p>}
                </div>
            </section>

            <ConfirmModal
                show={pendingRemove !== null}
                title={pendingRemove === "filter" ? "Remove filtered items?" : "Remove selected items?"}
                message={removeConfirmMessage}
                confirmText="Remove"
                cancelText="Cancel"
                onCancel={() => setPendingRemove(null)}
                onConfirm={handleConfirmRemove}
            />
        </div>
    );
}

function SourceRow({ source }: { source: WatchtowerSource }) {
    const fetcher = useFetcher();
    const label = sourceLabel(source);
    const host = source.url ? hostOf(source.url) : "";
    return (
        <div className={`flex flex-col items-stretch gap-2.5 px-2.5 py-3 transition-colors hover:bg-base-200/40 sm:flex-row sm:items-center sm:justify-between ${source.enabled ? "" : "opacity-50"}`}>
            <div className="flex min-w-0 flex-1 flex-wrap items-center gap-2.5 sm:flex-nowrap">
                <Badge className="badge-ghost badge-sm shrink-0 uppercase">{kindLabel(source.kind)}</Badge>
                <div className="min-w-0">
                    <div className="truncate font-medium text-base-content" title={source.url ?? undefined}>{label}</div>
                    {host && host !== label && <div className="text-xs text-base-content/50">{host}</div>}
                </div>
            </div>
            <div className="flex flex-wrap items-center gap-2">
                {source.cap > 0 && <span className="text-xs text-base-content/50">cap {source.cap}</span>}
                {source.lastSyncError
                    ? <span className="text-xs text-error" title={source.lastSyncError}>sync error</span>
                    : source.lastSyncedAtUnix
                        ? <span className="text-xs text-success">synced {formatAge(source.lastSyncedAtUnix)}</span>
                        : <span className="text-xs text-base-content/50">not synced yet</span>}
                {source.url && (
                    <fetcher.Form method="post">
                        <input type="hidden" name="action" value="sync-source" />
                        <input type="hidden" name="id" value={source.id} />
                        <button type="submit" className="btn btn-ghost btn-xs" disabled={fetcher.state !== "idle"}>sync now</button>
                    </fetcher.Form>
                )}
                <fetcher.Form method="post">
                    <input type="hidden" name="action" value="set-source-scope" />
                    <input type="hidden" name="id" value={source.id} />
                    <Form.Select
                        name="seriesScope"
                        defaultValue={source.seriesScope ?? ""}
                        className="select-sm max-w-[170px]"
                        title="Series scope for this list"
                        onChange={(e) => e.currentTarget.form?.requestSubmit()}
                    >
                        {SCOPE_OPTIONS.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
                    </Form.Select>
                </fetcher.Form>
                <fetcher.Form method="post">
                    <input type="hidden" name="action" value="toggle-source" />
                    <input type="hidden" name="id" value={source.id} />
                    <input type="hidden" name="enabled" value={String(!source.enabled)} />
                    <button type="submit" className="btn btn-ghost btn-xs">{source.enabled ? "disable" : "enable"}</button>
                </fetcher.Form>
                <fetcher.Form method="post">
                    <input type="hidden" name="action" value="remove-source" />
                    <input type="hidden" name="id" value={source.id} />
                    <button type="submit" className="btn btn-ghost btn-xs text-error">remove</button>
                </fetcher.Form>
            </div>
        </div>
    );
}

function ItemRow({ item, selected, onToggleSelect }: {
    item: WatchtowerItem;
    selected: boolean;
    onToggleSelect: (key: string) => void;
}) {
    const fetcher = useFetcher<typeof action>();
    const pending = fetcher.formData?.get("action");
    const removing = pending === "remove-item";
    const checking = pending === "recheck-item";
    const error = fetcher.data && fetcher.data.ok === false ? fetcher.data.error : null;
    return (
        <div className={`flex flex-col items-stretch gap-2.5 px-2.5 py-3 transition-colors hover:bg-base-200/40 sm:flex-row sm:items-center sm:justify-between ${removing ? "opacity-50" : ""} ${selected ? "bg-primary/10 shadow-[inset_2px_0_0_0] shadow-primary" : ""}`}>
            <div className="flex min-w-0 flex-1 flex-wrap items-center gap-2.5 sm:flex-nowrap">
                <Checkbox
                    checked={selected}
                    onChange={() => onToggleSelect(item.key)}
                    aria-label={`Select ${item.title}`}
                    className="checkbox-sm shrink-0"
                />
                <StateChip state={item.state} />
                <div className="min-w-0">
                    <div className="truncate font-medium text-base-content" title={item.title}>{item.title}</div>
                    <div className="mt-0.5 flex flex-wrap items-center gap-2 text-xs text-base-content/50">
                        <Badge className="badge-ghost badge-xs uppercase">{item.type === "season" ? "season bundle" : item.type}</Badge>
                        <span className="font-mono">{item.contentId}</span>
                        {item.provenanceCount > 1 && <span>on {item.provenanceCount} lists</span>}
                        {item.state === "ready" && <>
                            {item.winnerTitle && <span className="max-w-[260px] truncate font-mono" title={item.winnerTitle}>{item.winnerTitle}</span>}
                            <span>{formatBytes(item.winnerSize)} · {item.shortlistCount} pointer{item.shortlistCount === 1 ? "" : "s"}</span>
                            {item.lastVerifiedAtUnix && <span>verified {formatAge(item.lastVerifiedAtUnix)}</span>}
                            {item.nextCheckAtUnix && <span>re-checks {formatWhen(item.nextCheckAtUnix)}</span>}
                        </>}
                        {item.state === "unavailable" && <>
                            {item.failReason && <span>{item.failReason}</span>}
                            {item.nextCheckAtUnix && <span>retries {formatWhen(item.nextCheckAtUnix)}</span>}
                        </>}
                        {item.state === "parked" && item.failReason && <span>{item.failReason}</span>}
                        {item.state === "scouting" && <span>searching…</span>}
                    </div>
                </div>
            </div>
            <div className="flex flex-wrap items-center gap-2">
                {error && <span className="text-xs text-error" title={error}>failed — retry</span>}
                <fetcher.Form method="post">
                    <input type="hidden" name="action" value="recheck-item" />
                    <input type="hidden" name="key" value={item.key} />
                    <button type="submit" className="btn btn-ghost btn-xs" disabled={fetcher.state !== "idle"}>{checking ? "checking…" : "check now"}</button>
                </fetcher.Form>
                <fetcher.Form method="post">
                    <input type="hidden" name="action" value="remove-item" />
                    <input type="hidden" name="key" value={item.key} />
                    <button type="submit" className="btn btn-ghost btn-xs text-error" disabled={fetcher.state !== "idle"}>{removing ? "removing…" : "remove"}</button>
                </fetcher.Form>
            </div>
        </div>
    );
}

function ExpanderGroup({ expander, episodes, expanded, childrenLoaded, canToggle, onToggle, selectedKeys, onToggleSelect, onSelectMany }: {
    expander: WatchtowerItem;
    episodes: WatchtowerItem[];
    expanded: boolean;
    childrenLoaded: boolean;
    canToggle: boolean;
    onToggle: () => void;
    selectedKeys: Set<string>;
    onToggleSelect: (key: string) => void;
    onSelectMany: (keys: string[], select: boolean) => void;
}) {
    const fetcher = useFetcher<typeof action>();
    const seriesCheckRef = useRef<HTMLInputElement>(null);
    const pending = fetcher.formData?.get("action");
    const removing = pending === "remove-item";
    const checking = pending === "recheck-item";
    const error = fetcher.data && fetcher.data.ok === false ? fetcher.data.error : null;
    const loaded = episodes.filter(c => c.state !== "parked");
    const totalCount = expander.childTotal ?? loaded.length;
    const ready = expander.childReady ?? loaded.filter(c => c.state === "ready").length;
    const unavailable = expander.childUnavailable ?? loaded.filter(c => c.state === "unavailable").length;
    const sorted = [...episodes].sort((a, b) => a.contentId.localeCompare(b.contentId, undefined, { numeric: true }));

    const childKeys = episodes.map(c => c.key);
    const allSel = childKeys.length > 0 && childKeys.every(k => selectedKeys.has(k));
    const someSel = childKeys.some(k => selectedKeys.has(k));
    const pct = totalCount > 0 ? Math.round((ready / totalCount) * 100) : 0;

    useEffect(() => {
        if (seriesCheckRef.current) seriesCheckRef.current.indeterminate = someSel && !allSel;
    }, [someSel, allSel]);

    return (
        <div className="divide-y divide-base-content/10">
            <div
                className={`flex min-w-0 items-center gap-2.5 px-2.5 py-3 transition-colors ${canToggle ? "cursor-pointer hover:bg-base-200/40" : ""} ${expanded ? "border-b border-base-content/10" : ""} ${removing ? "opacity-50" : ""}`}
                role="button"
                tabIndex={0}
                aria-expanded={expanded}
                onClick={() => canToggle && onToggle()}
                onKeyDown={e => {
                    if (canToggle && (e.key === "Enter" || e.key === " ") && e.target === e.currentTarget) {
                        e.preventDefault();
                        onToggle();
                    }
                }}
            >
                <span onClick={e => e.stopPropagation()}>
                    <Checkbox
                        ref={seriesCheckRef}
                        checked={allSel}
                        disabled={childKeys.length === 0}
                        onChange={() => onSelectMany(childKeys, !allSel)}
                        aria-label={`Select loaded episodes of ${expander.title}`}
                        className="checkbox-sm shrink-0"
                    />
                </span>
                <Icon
                    name="chevron_right"
                    className={`shrink-0 !text-[16px] text-base-content/50 transition-transform duration-200 ${expanded ? "rotate-90" : ""}`}
                />
                <Badge className="badge-ghost badge-sm shrink-0 uppercase">Show</Badge>
                <div className="min-w-0 flex-1">
                    <div className="truncate font-medium text-base-content" title={expander.title}>{expander.title}</div>
                    <div className="mt-0.5 flex flex-wrap items-center gap-2 text-xs text-base-content/50">
                        <span className="font-mono">{expander.contentId}</span>
                        {totalCount === 0
                            ? <span>expanding…</span>
                            : <span className="inline-flex items-center gap-2">
                                <progress className="progress progress-success h-1 w-20" value={ready} max={totalCount} />
                                <span>{ready}/{totalCount} ready ({pct}%)</span>
                              </span>}
                        {unavailable > 0 && <span className="text-error">{unavailable} unavailable</span>}
                    </div>
                </div>
                <div className="flex flex-wrap items-center gap-2" onClick={e => e.stopPropagation()}>
                    {error && <span className="text-xs text-error" title={error}>failed — retry</span>}
                    <fetcher.Form method="post">
                        <input type="hidden" name="action" value="recheck-item" />
                        <input type="hidden" name="key" value={expander.key} />
                        <button type="submit" className="btn btn-ghost btn-xs" disabled={fetcher.state !== "idle"}>{checking ? "checking…" : "check now"}</button>
                    </fetcher.Form>
                    <fetcher.Form method="post">
                        <input type="hidden" name="action" value="remove-item" />
                        <input type="hidden" name="key" value={expander.key} />
                        <button type="submit" className="btn btn-ghost btn-xs text-error" disabled={fetcher.state !== "idle"}>{removing ? "removing…" : "remove"}</button>
                    </fetcher.Form>
                </div>
            </div>
            {expanded && (
                sorted.length > 0
                    ? <div className="ml-6 border-l-2 border-base-content/10 pl-3 animate-reveal">
                        {sorted.map(c => <ItemRow key={c.key} item={c} selected={selectedKeys.has(c.key)} onToggleSelect={onToggleSelect} />)}
                      </div>
                    : <div className="ml-6 border-l-2 border-base-content/10 pl-3 animate-reveal">
                        <p className="px-2.5 py-2 text-xs text-base-content/50">{childrenLoaded ? "No episodes yet." : "Loading episodes…"}</p>
                      </div>
            )}
        </div>
    );
}

function StateChip({ state }: { state: string }) {
    const label = state === "ready" ? "Ready"
        : state === "unavailable" ? "Unavailable"
        : state === "parked" ? "Parked"
        : state === "expander" ? "Show"
        : "Scouting";
    const cls = state === "ready" ? "badge-success"
        : state === "unavailable" ? "badge-error"
        : state === "parked" ? "badge-ghost"
        : state === "expander" ? "badge-ghost"
        : "badge-warning";
    return <Badge className={`badge-sm shrink-0 uppercase ${cls}`}>{label}</Badge>;
}

function StatButton({ label, value, tone, active, onClick }: { label: string, value: number, tone?: "ok" | "warn" | "bad", active?: boolean, onClick?: () => void }) {
    const valueClass = tone === "ok" ? "text-success"
        : tone === "warn" ? "text-warning"
        : tone === "bad" ? "text-error"
        : "";
    return (
        <button
            type="button"
            className={`stat px-4 py-2 transition-colors ${active ? "bg-base-200" : "hover:bg-base-200/50"}`}
            onClick={onClick}
        >
            <div className="stat-title text-[10px] uppercase tracking-wider">{label}</div>
            <div className={`stat-value font-mono text-xl ${valueClass}`}>{value}</div>
        </button>
    );
}

function formatBytes(bytes: number): string {
    if (bytes <= 0) return "-";
    const u = ["B", "KB", "MB", "GB", "TB"];
    let i = 0;
    let v = bytes;
    while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
    return `${v.toFixed(v >= 100 ? 0 : v >= 10 ? 1 : 2)} ${u[i]}`;
}

function formatAge(unixSeconds: number): string {
    const age = Math.max(0, Math.floor(Date.now() / 1000 - unixSeconds));
    if (age < 5) return "just now";
    if (age < 60) return `${age}s ago`;
    if (age < 3600) return `${Math.floor(age / 60)}m ago`;
    if (age < 86400) return `${Math.floor(age / 3600)}h ago`;
    return `${Math.floor(age / 86400)}d ago`;
}

function formatWhen(unixSeconds: number): string {
    const d = Math.floor(unixSeconds - Date.now() / 1000);
    if (d <= 0) return "soon";
    if (d < 60) return `in ${d}s`;
    if (d < 3600) return `in ${Math.floor(d / 60)}m`;
    if (d < 86400) return `in ${Math.floor(d / 3600)}h`;
    return `in ${Math.floor(d / 86400)}d`;
}

function kindLabel(kind: string): string {
    if (kind === "stremio-catalog") return "catalog";
    if (kind === "url-list") return "url list";
    return kind;
}

function titleCase(value: string): string {
    return value
        .replace(/[-_]+/g, " ")
        .replace(/\s+/g, " ")
        .trim()
        .split(" ")
        .map(w => (w ? w[0].toUpperCase() + w.slice(1) : w))
        .join(" ");
}

function hostOf(raw: string): string {
    try {
        return new URL(raw).hostname.replace(/^www\./, "");
    } catch {
        return "";
    }
}

function labelFromUrl(raw: string): string {
    let parsed: URL;
    try {
        parsed = new URL(raw);
    } catch {
        return raw;
    }
    const parts = parsed.pathname.split("/").filter(Boolean).map(p => {
        try { return decodeURIComponent(p); } catch { return p; }
    });
    const ci = parts.indexOf("catalog");
    if (ci >= 0 && parts.length > ci + 2) {
        const type = parts[ci + 1];
        const id = parts[ci + 2].replace(/\.json$/i, "");
        const pretty = titleCase(id);
        return type ? `${pretty} · ${titleCase(type)}` : pretty;
    }
    const last = parts.length ? parts[parts.length - 1].replace(/\.json$/i, "") : "";
    return last ? titleCase(last) : hostOf(raw);
}

function sourceLabel(source: WatchtowerSource): string {
    const name = (source.name ?? "").trim();
    const url = (source.url ?? "").trim();
    if (name && name !== url) return name;
    if (url) return labelFromUrl(url);
    return name || "Untitled list";
}
