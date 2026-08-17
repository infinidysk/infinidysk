import type { HealthCheckResult } from "~/clients/backend-client.server";
import { Badge, Icon } from "~/components/ui";
import { Pagination } from "~/routes/queue/components/pagination/pagination";
import { Truncate } from "~/routes/queue/components/truncate/truncate";

export type HealthHistoryFilter = "all" | "deleted" | "repaired";

export type HealthHistoryTableProps = {
    items: HealthCheckResult[],
    totalCount: number,
    page: number,
    pageSize: number,
    pageSizeOptions: readonly number[],
    filter: HealthHistoryFilter,
    refreshing: boolean,
    onFilterSelected: (filter: HealthHistoryFilter) => void,
    onPageSelected: (page: number) => void,
    onPageSizeSelected: (pageSize: number) => void,
    onRefresh: () => void,
}

const desktopHeaderClass =
    "hidden min-[900px]:table-cell px-3 py-3 text-left text-xs font-semibold uppercase tracking-wide";
const desktopCellClass =
    "hidden min-[900px]:table-cell max-w-[240px] px-3 py-3 align-top text-xs text-base-content/70";
// Numeric value mirrors the backend RepairAction enum declared in
// ~/clients/backend-client.server (a .server module, so its enums cannot be value-imported here).
const RepairActionDeleted: HealthCheckResult["repairStatus"] = 2;

export function HealthHistoryTable({
    items,
    totalCount,
    page,
    pageSize,
    pageSizeOptions,
    filter,
    refreshing,
    onFilterSelected,
    onPageSelected,
    onPageSizeSelected,
    onRefresh,
}: HealthHistoryTableProps) {
    const totalPages = Math.max(1, Math.ceil(totalCount / pageSize));

    return (
        <section className="card w-full border border-base-content/10 bg-base-100 shadow-sm">
            <div className="card-body gap-0 p-0">
                <div className="flex flex-wrap items-center justify-between gap-3 border-b border-base-content/10 px-4 py-4 md:px-6">
                    <div>
                        <h2 className="card-title text-xl">Repair history</h2>
                        <p className="mt-1 text-xs text-base-content/60">
                            Deleted and repaired items are retained according to your health-check retention setting.
                        </p>
                    </div>
                    <button
                        type="button"
                        className="btn btn-primary btn-sm gap-2"
                        onClick={onRefresh}
                        disabled={refreshing}
                    >
                        <Icon name="refresh" className={`!text-[16px] ${refreshing ? "animate-spin" : ""}`} />
                        Refresh
                    </button>
                </div>

                <div className="flex flex-wrap items-center justify-between gap-3 border-b border-base-content/10 px-4 py-3 md:px-6">
                    <div className="join flex-wrap">
                        <FilterButton active={filter === "all"} onClick={() => onFilterSelected("all")}>
                            Deleted &amp; repaired
                        </FilterButton>
                        <FilterButton active={filter === "deleted"} onClick={() => onFilterSelected("deleted")}>
                            Deleted
                        </FilterButton>
                        <FilterButton active={filter === "repaired"} onClick={() => onFilterSelected("repaired")}>
                            Repaired
                        </FilterButton>
                    </div>
                    {totalCount > 0 && (
                        <Badge className="badge-ghost badge-sm font-mono tabular-nums">
                            {totalCount}
                        </Badge>
                    )}
                </div>

                {items.length === 0 ? (
                    <EmptyState filter={filter} />
                ) : (
                    <>
                        <div className="overflow-x-auto">
                            <table className="table table-zebra table-sm mb-0 w-full min-w-0 text-base-content min-[900px]:min-w-[900px]">
                                <thead>
                                    <tr className="border-base-content/10 [&_th]:bg-base-200 [&_th]:text-base-content/70">
                                        <th className="py-3 pl-4 text-left text-xs font-semibold uppercase tracking-wide md:pl-6">
                                            NZB
                                        </th>
                                        <th className={desktopHeaderClass}>Status</th>
                                        <th className={desktopHeaderClass}>Reason</th>
                                        <th className={`${desktopHeaderClass} pr-4 md:pr-6`}>When</th>
                                    </tr>
                                </thead>
                                <tbody>
                                    {items.map(item => <HistoryRow key={item.id} item={item} />)}
                                </tbody>
                            </table>
                        </div>
                        <div className="border-t border-base-content/10 px-4 py-3 md:px-6">
                            <Pagination
                                pageNumber={page}
                                totalPages={totalPages}
                                totalCount={totalCount}
                                pageSize={pageSize}
                                pageSizeOptions={pageSizeOptions}
                                onPageSelected={onPageSelected}
                                onPageSizeSelected={onPageSizeSelected}
                            />
                        </div>
                    </>
                )}
            </div>
        </section>
    );
}

function FilterButton({
    active,
    onClick,
    children,
}: {
    active: boolean;
    onClick: () => void;
    children: React.ReactNode;
}) {
    return (
        <button
            type="button"
            className={`btn btn-sm join-item ${active ? "btn-primary" : "btn-ghost"}`}
            onClick={onClick}
        >
            {children}
        </button>
    );
}

function HistoryRow({ item }: { item: HealthCheckResult }) {
    const title = item.nzbFileName ?? basename(item.path);
    const timestamp = formatTimestamp(item.createdAt);

    return (
        <tr className="border-base-content/10">
            <td className="max-w-[320px] py-3 pl-4 align-top md:pl-6 max-[899px]:max-w-none">
                <div className="flex min-w-0 flex-col gap-1">
                    <div className="break-all text-sm font-medium leading-snug text-base-content">
                        <Truncate>{title}</Truncate>
                    </div>
                    {item.jobName && item.jobName !== title && (
                        <div className="break-all text-xs text-base-content/60">
                            <Truncate>{item.jobName}</Truncate>
                        </div>
                    )}
                    <div className="break-all text-xs leading-snug text-base-content/45">
                        <Truncate>{item.path}</Truncate>
                    </div>
                    <div className="mt-1 flex flex-wrap gap-x-3 gap-y-1 min-[900px]:hidden">
                        <StatusBadge item={item} />
                        <MetaChip label="When" value={timestamp.relative} title={timestamp.absolute} />
                        {item.message && <MetaChip label="Reason" value={item.message} title={item.message} />}
                    </div>
                </div>
            </td>
            <td className={desktopCellClass}>
                <StatusBadge item={item} />
            </td>
            <td className={desktopCellClass}>
                <div className="line-clamp-3 leading-snug" title={item.message ?? undefined}>
                    {item.message ?? "—"}
                </div>
            </td>
            <td className={`${desktopCellClass} pr-4 font-mono tabular-nums md:pr-6`} title={timestamp.absolute}>
                <time dateTime={item.createdAt}>{timestamp.relative}</time>
            </td>
        </tr>
    );
}

function StatusBadge({ item }: { item: HealthCheckResult }) {
    const deleted = item.repairStatus === RepairActionDeleted;
    return (
        <Badge className={`badge-sm ${deleted ? "badge-error" : "badge-info"}`}>
            {deleted ? "Deleted" : "Repaired"}
        </Badge>
    );
}

function MetaChip({ label, value, title }: { label: string; value: string; title?: string }) {
    return (
        <span className="inline-flex max-w-full items-center gap-1.5 text-[11px] text-base-content/55">
            <span className="shrink-0 uppercase tracking-wide text-base-content/40">{label}</span>
            <span className="truncate font-mono tabular-nums text-base-content/70" title={title}>{value}</span>
        </span>
    );
}

function EmptyState({ filter }: { filter: HealthHistoryFilter }) {
    const label = filter === "all" ? "deleted or repaired" : filter;
    return (
        <div className="hero min-h-[220px] py-8">
            <div className="hero-content">
                <div className="flex max-w-md flex-col items-center text-center">
                    <Icon name="history" className="mb-3 !text-[48px] text-base-content/40" />
                    <h3 className="text-base font-semibold text-base-content">No {label} items</h3>
                    <p className="mt-1 text-xs leading-relaxed text-base-content/60">
                        New automatic health repairs and deletions will appear here until health-check retention removes them.
                    </p>
                </div>
            </div>
        </div>
    );
}

function basename(path: string) {
    const segments = path.split('/').filter(Boolean);
    return segments.at(-1) ?? path;
}

function formatTimestamp(value: string) {
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return { relative: "Unknown", absolute: "Unknown" };

    const seconds = Math.max(0, Math.floor((Date.now() - date.getTime()) / 1000));
    const relative = seconds < 5 ? "just now"
        : seconds < 60 ? `${seconds}s ago`
            : seconds < 3600 ? `${Math.floor(seconds / 60)}m ago`
                : seconds < 86400 ? `${Math.floor(seconds / 3600)}h ago`
                    : `${Math.floor(seconds / 86400)}d ago`;
    return { relative, absolute: date.toLocaleString() };
}
