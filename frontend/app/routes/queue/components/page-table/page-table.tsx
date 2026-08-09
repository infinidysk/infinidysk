import type { ReactNode } from "react";
import { Link } from "react-router";
import { TriCheckbox, type TriCheckboxState } from "../tri-checkbox/tri-checkbox";
import { Truncate } from "../truncate/truncate";
import { StatusBadge } from "../status-badge/status-badge";
import { formatFileSize } from "~/utils/file-size";
import type { ProviderUsage } from "~/clients/backend-client.server";
import { Badge } from "~/components/ui";

const desktopHeaderClass = "hidden min-[900px]:table-cell w-[120px] text-center text-xs font-semibold uppercase tracking-wide";
const desktopCellClass = "hidden min-[900px]:table-cell max-w-[200px] min-w-0 overflow-hidden whitespace-nowrap px-1 py-3 text-center align-middle";
const statusCellClass = "hidden min-[900px]:table-cell max-w-[200px] min-w-0 overflow-visible whitespace-nowrap px-1 py-3 text-center align-middle";
const providerCellClass = "hidden min-[900px]:table-cell max-w-[200px] min-w-0 overflow-hidden px-1 py-3 text-center align-middle";

export type PageTableProps = {
    children?: ReactNode,
    headerCheckboxState: TriCheckboxState,
    onHeaderCheckboxChange: (isChecked: boolean) => void,
    footer?: ReactNode,
    showCompleted?: boolean,
    selectable?: boolean,
}

export function PageTable({ children, headerCheckboxState, onHeaderCheckboxChange, footer, showCompleted, selectable = true }: PageTableProps) {
    return (
        <div className="-mx-4 overflow-x-auto sm:-mx-6">
            <table className="table table-zebra table-sm mb-0 w-full min-w-0 text-base-content min-[900px]:min-w-[880px]">
                <thead>
                    <tr className="border-base-content/10 [&_th]:bg-base-200 [&_th]:text-base-content/70">
                        <th className="min-[900px]:w-1/2 w-auto py-4 pl-0 text-left text-xs font-semibold uppercase tracking-wide">
                            {selectable
                                ? <TriCheckbox state={headerCheckboxState} onChange={onHeaderCheckboxChange}>Name</TriCheckbox>
                                : "Name"}
                        </th>
                        <th className={desktopHeaderClass}>Category</th>
                        <th className={desktopHeaderClass}>Indexer</th>
                        <th className={desktopHeaderClass}>Provider</th>
                        <th className={desktopHeaderClass}>Status</th>
                        <th className={desktopHeaderClass}>Size</th>
                        {showCompleted && <th className={desktopHeaderClass}>Completed</th>}
                        <th className="w-[100px] py-4 text-center text-xs font-semibold">Actions</th>
                    </tr>
                </thead>
                <tbody>
                    {children}
                </tbody>
            </table>
            {footer &&
                <div className="py-3 text-center">{footer}</div>
            }
        </div>
    );
}

export type PageRowProps = {
    isUploading?: boolean,
    isSelected: boolean,
    isRemoving: boolean,
    name: string,
    nameHref?: string | null,
    category: string,
    status: string,
    percentage?: string | undefined,
    error?: string | undefined,
    fileSizeBytes: number,
    /** Unix seconds from SAB history `completed` field. */
    completed?: number | null,
    showCompleted?: boolean,
    actions: ReactNode,
    indexer?: string | null | undefined,
    providers?: ProviderUsage[] | null | undefined,
    onRowSelectionChanged: (isSelected: boolean) => void,
    selectable?: boolean,
}
export function PageRow(props: PageRowProps) {
    const nameContent = props.nameHref
        ? <Link to={props.nameHref} discover="none" className="text-base-content hover:text-primary hover:underline" onClick={e => e.stopPropagation()}>{props.name}</Link>
        : props.name;
    const completedLabel = formatCompleted(props.completed);

    return (
        <tr className={`${props.isRemoving ? "opacity-20" : ""} ${props.isUploading ? "bg-primary/5 [&+tr]:border-t-[3px] [&+tr]:border-base-300" : ""}`}>
            <td className="max-w-[200px] whitespace-nowrap py-3 pl-0 pr-1 text-left align-middle min-[900px]:max-w-[200px] max-[899px]:max-w-none max-[899px]:whitespace-normal">
                {props.selectable === false ? (
                    <>
                        <Truncate>{nameContent}</Truncate>
                        <MobileRowDetails {...props} completedLabel={completedLabel} />
                    </>
                ) : <TriCheckbox state={props.isSelected} onChange={props.onRowSelectionChanged}>
                    <Truncate>{nameContent}</Truncate>
                    <MobileRowDetails {...props} completedLabel={completedLabel} />
                </TriCheckbox>}
            </td>
            <td className={desktopCellClass}>
                <CategoryBadge category={props.category} />
            </td>
            <td className={desktopCellClass}>
                {props.indexer ? <IndexerBadge indexer={props.indexer} /> : <span className="text-base-content/30 text-xs">—</span>}
            </td>
            <td className={providerCellClass}>
                {props.providers && props.providers.length > 0
                    ? <ProvidersBadge providers={props.providers} />
                    : <span className="text-base-content/30 text-xs">—</span>}
            </td>
            <td className={statusCellClass}>
                <StatusBadge status={props.status} percentage={props.percentage} error={props.error} />
            </td>
            <td className="hidden min-[900px]:table-cell max-w-[200px] whitespace-nowrap px-1 py-3 text-center align-middle font-mono text-xs">
                {formatFileSize(props.fileSizeBytes)}
            </td>
            {props.showCompleted &&
                <td className="hidden min-[900px]:table-cell max-w-[200px] whitespace-nowrap px-1 py-3 text-center align-middle font-mono text-xs" title={completedLabel?.full}>
                    {completedLabel?.short ?? "—"}
                </td>
            }
            <td className="max-w-[200px] whitespace-nowrap px-1 py-3 text-center align-middle max-[899px]:max-w-none max-[899px]:whitespace-normal">
                <div className="flex flex-col items-end justify-center gap-2.5 pr-5 min-[410px]:flex-row min-[410px]:items-center min-[410px]:pr-0">
                    {props.actions}
                </div>
            </td>
        </tr>
    );
}

function MobileRowDetails(
    props: PageRowProps & { completedLabel: ReturnType<typeof formatCompleted> },
) {
    return (
        <div className="block min-[900px]:hidden">
            <div className="mb-1 mt-1 flex flex-wrap gap-2.5">
                <StatusBadge status={props.status} percentage={props.percentage} error={props.error} />
                <CategoryBadge category={props.category} />
                {props.indexer && <IndexerBadge indexer={props.indexer} />}
                {props.providers && props.providers.length > 0 && <ProvidersBadge providers={props.providers} />}
            </div>
            <div className="font-mono text-xs text-base-content/60">{formatFileSize(props.fileSizeBytes)}</div>
            {props.showCompleted && props.completedLabel &&
                <div className="font-mono text-xs text-base-content/60" title={props.completedLabel.full}>{props.completedLabel.short}</div>
            }
        </div>
    );
}

function formatCompleted(completed: number | null | undefined): { short: string, full: string } | null {
    if (completed == null || !Number.isFinite(completed) || completed <= 0) return null;
    try {
        const datetime = new Date(completed * 1000);
        const now = new Date();
        const full = datetime.toLocaleString();
        const short = isSameDate(datetime, now)
            ? datetime.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" })
            : datetime.toLocaleDateString();
        return { short, full };
    } catch {
        return null;
    }
}

function isSameDate(a: Date, b: Date): boolean {
    return a.getFullYear() === b.getFullYear()
        && a.getMonth() === b.getMonth()
        && a.getDate() === b.getDate();
}

export function CategoryBadge({ category }: { category: string }) {
    const categoryLower = category?.toLowerCase();
    return (
        <Badge
            className="badge-outline badge-sm min-w-[88px] max-w-[120px] shrink-0 justify-center whitespace-nowrap lowercase"
            title={`Category: ${categoryLower}`}
        >
            <span className="min-w-0 truncate">{categoryLower}</span>
        </Badge>
    );
}

export function IndexerBadge({ indexer }: { indexer: string }) {
    return (
        <Badge className="badge-dash badge-ghost badge-sm max-w-[110px] truncate" title={`Indexer: ${indexer}`}>
            via {indexer}
        </Badge>
    );
}

export function ProvidersBadge({ providers }: { providers: ProviderUsage[] }) {
    if (providers.length === 0) return null;
    const total = providers.reduce((acc, p) => acc + p.segments, 0);
    // When usage exists, hide idle (0%) hosts from the badge; keep them in the tooltip.
    const visible = total > 0 ? providers.filter(p => p.segments > 0) : providers;
    const labelOf = (p: ProviderUsage) => p.nickname?.trim() || stripHost(p.host);
    const tooltip = providers
        .map(p => total > 0
            ? `${labelOf(p)} (${p.host}): ${p.segments} segments (${Math.round((p.segments / total) * 100)}%)`
            : `${labelOf(p)} (${p.host}): idle`)
        .join("\n");
    return (
        <span
            className="inline-flex max-w-full min-w-0 cursor-help flex-col items-stretch gap-0.5 text-left text-xs"
            title={tooltip}
        >
            {visible.map((p, i) => (
                <span key={`${p.host}-${i}`} className="flex min-w-0 items-baseline gap-1 overflow-hidden">
                    <span className="min-w-0 truncate">{labelOf(p)}</span>
                    {total > 0 && (
                        <span className="shrink-0 tabular-nums text-base-content/50">
                            {Math.round((p.segments / total) * 100)}%
                        </span>
                    )}
                </span>
            ))}
        </span>
    );
}

// Generic NNTP hostname prefixes that aren't brand-identifying.
const GENERIC_HOST_PREFIXES = new Set(["news", "reader", "premium", "secure", "ssl", "nntp", "usenet", "block"]);

function stripHost(host: string): string {
    if (!host) return "—";
    const labels = host.split(".").filter(Boolean);
    const first = labels[0];
    if (first === undefined) return host;
    const second = labels[1];
    if (second === undefined) return first;
    if (labels.length === 2) return first;
    // 3+ labels: skip a generic prefix to get to the brand label
    if (GENERIC_HOST_PREFIXES.has(first.toLowerCase())) return second;
    // pick whichever of the first two is longer (heuristic for "more identifying")
    return first.length >= second.length ? first : second;
}
