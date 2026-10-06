import type { HealthCheckResult } from "~/clients/backend-client.server";
import { Badge, Button, Icon, PortalTooltip, RadioJoinFilter } from "~/components/ui";
import { Pagination } from "~/components/pagination/pagination";
import { Truncate } from "~/components/truncate/truncate";

export type HealthHistoryFilter = "all" | "deleted" | "repaired" | "degraded";

export type HealthHistoryTableProps = {
  items: HealthCheckResult[];
  totalCount: number;
  page: number;
  pageSize: number;
  pageSizeOptions: readonly number[];
  filter: HealthHistoryFilter;
  onFilterSelected: (filter: HealthHistoryFilter) => void;
  onPageSelected: (page: number) => void;
  onPageSizeSelected: (pageSize: number) => void;
};

const cardClass = "card w-full border border-base-content/10 bg-base-100 shadow-sm";
const cardHeaderClass =
  "flex flex-wrap items-center justify-between gap-3 border-b border-base-content/10 px-4 py-4 md:px-6";
const headRowClass = "border-base-content/10 [&_th]:bg-base-200 [&_th]:text-base-content/70";
const firstHeaderClass =
  "py-3 pl-4 text-left text-xs font-semibold uppercase tracking-wide md:pl-6";

const desktopHeaderClass =
  "hidden min-[900px]:table-cell px-3 py-3 text-left text-xs font-semibold uppercase tracking-wide";
const desktopCellClass =
  "hidden min-[900px]:table-cell max-w-[240px] px-3 py-3 align-top text-xs text-base-content/70";
// Numeric values mirror the backend HealthResult / RepairAction enums declared in
// ~/clients/backend-client.server (a .server module, so its enums cannot be value-imported here).
const RepairActionDeleted: HealthCheckResult["repairStatus"] = 2;
const RepairActionNeeded: HealthCheckResult["repairStatus"] = 3;
const HealthResultDegraded: HealthCheckResult["result"] = 2;

export function HealthHistoryTable({
  items,
  totalCount,
  page,
  pageSize,
  pageSizeOptions,
  filter,
  onFilterSelected,
  onPageSelected,
  onPageSizeSelected,
}: HealthHistoryTableProps) {
  const totalPages = Math.max(1, Math.ceil(totalCount / pageSize));

  return (
    <section id="repair-history" className={`${cardClass} scroll-mt-4`}>
      <div className="card-body gap-0 p-0">
        <div className={cardHeaderClass}>
          <div>
            <h2 className="card-title text-xl">Health history</h2>
            <p className="mt-1 text-xs text-base-content/60">
              Repairs, deletions, and degraded checks are retained according to your health-check
              retention setting.
            </p>
          </div>
        </div>

        <div className="border-b border-base-content/10 px-4 py-3 md:px-6">
          <RadioJoinFilter
            name="health-history-filter"
            aria-label="Health history status filter"
            value={filter}
            onChange={onFilterSelected}
            options={[
              { id: "all", label: "All actions" },
              { id: "deleted", label: "Deleted" },
              { id: "repaired", label: "Repaired" },
              { id: "degraded", label: "Degraded" },
            ]}
          />
        </div>

        {items.length === 0 ? (
          <EmptyState filter={filter} />
        ) : (
          <>
            <div className="overflow-x-auto">
              <table className="table table-zebra table-sm mb-0 w-full min-w-0 text-base-content min-[900px]:min-w-[900px]">
                <thead>
                  <tr className={headRowClass}>
                    <th className={firstHeaderClass}>NZB</th>
                    <th className={desktopHeaderClass}>Status</th>
                    <th className={desktopHeaderClass}>Reason</th>
                    <th className={`${desktopHeaderClass} pr-4 md:pr-6`}>When</th>
                  </tr>
                </thead>
                <tbody>
                  {items.map((item) => (
                    <HistoryRow key={item.id} item={item} />
                  ))}
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

export function HealthAttentionTable({
  items,
  totalCount,
  page,
  pageSize,
  pageSizeOptions,
  onPageSelected,
  onPageSizeSelected,
  canRequeueActionNeeded,
  requeueingActionNeeded,
  onRequeueActionNeeded,
  onDelete,
}: Omit<HealthHistoryTableProps, "filter" | "onFilterSelected"> & {
  canRequeueActionNeeded: boolean;
  requeueingActionNeeded: boolean;
  onRequeueActionNeeded: (davItemId?: string) => void;
  onDelete?: ((item: HealthCheckResult) => void) | undefined;
}) {
  return (
    <section aria-labelledby="health-attention-heading" className={cardClass}>
      <div className="card-body gap-0 p-0">
        <div className={cardHeaderClass}>
          <div className="flex items-center gap-2">
            <h2 id="health-attention-heading" className="card-title text-xl">
              Needs attention
            </h2>
            {totalCount > 0 && (
              <Badge className="badge-sm badge-warning badge-soft font-mono tabular-nums">
                {totalCount.toLocaleString()}
              </Badge>
            )}
          </div>
          {canRequeueActionNeeded && totalCount > 0 && (
            <Button onClick={() => onRequeueActionNeeded()} disabled={requeueingActionNeeded}>
              <Icon
                name={requeueingActionNeeded ? "progress_activity" : "replay"}
                className={`!text-[16px] ${requeueingActionNeeded ? "animate-spin" : ""}`}
              />
              {requeueingActionNeeded ? "Queueing..." : "Re-check action needed"}
            </Button>
          )}
        </div>
        {items.length === 0 ? (
          <div className="flex items-center gap-3 px-4 py-5 md:px-6">
            <Icon name="check_circle" filled className="shrink-0 !text-[28px] text-success" />
            <div>
              <p className="font-medium text-base-content">All clear</p>
              <p className="text-xs text-base-content/60">
                No files need a repair decision right now.
              </p>
            </div>
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="table table-zebra table-sm mb-0 w-full min-w-0 table-fixed text-base-content min-[900px]:table-auto">
              <thead>
                <tr className={headRowClass}>
                  <th className={firstHeaderClass}>NZB</th>
                  <th className={desktopHeaderClass}>Status &amp; reason</th>
                  <th className={desktopHeaderClass}>Last checked</th>
                  {(canRequeueActionNeeded || onDelete) && (
                    <th className={`${desktopHeaderClass} pr-4 text-right md:pr-6`}>
                      <span className="sr-only">Actions</span>
                    </th>
                  )}
                </tr>
              </thead>
              <tbody>
                {items.map((item) => (
                  <HistoryRow
                    key={item.id}
                    item={item}
                    combineStatusReason
                    requeueing={requeueingActionNeeded}
                    onDelete={onDelete ? () => onDelete(item) : undefined}
                    onRequeue={
                      canRequeueActionNeeded
                        ? () => onRequeueActionNeeded(item.davItemId)
                        : undefined
                    }
                  />
                ))}
              </tbody>
            </table>
          </div>
        )}
        {totalCount > 0 && (
          <div className="border-t border-base-content/10 px-4 py-3 md:px-6">
            <Pagination
              pageNumber={page}
              totalPages={Math.max(1, Math.ceil(totalCount / pageSize))}
              totalCount={totalCount}
              pageSize={pageSize}
              pageSizeOptions={pageSizeOptions}
              onPageSelected={onPageSelected}
              onPageSizeSelected={onPageSizeSelected}
            />
          </div>
        )}
      </div>
    </section>
  );
}

function HistoryRow({
  item,
  onRequeue,
  requeueing,
  onDelete,
  combineStatusReason = false,
}: {
  item: HealthCheckResult;
  combineStatusReason?: boolean;
  onRequeue?: (() => void) | undefined;
  requeueing?: boolean;
  onDelete?: (() => void) | undefined;
}) {
  const title = item.jobName || item.nzbFileName || basename(item.path);
  const timestamp = formatTimestamp(item.createdAt);
  const libraryLinkBadge = combineStatusReason &&
    item.message?.includes(
      "No corresponding imported symlink or .strm file was found in Library Directory.",
    ) && <Badge className="badge-sm badge-warning badge-outline">Not library linked</Badge>;
  const actions = (onRequeue || onDelete) && (
    <div className="flex gap-1 min-[900px]:justify-end">
      {onRequeue && (
        <PortalTooltip content="Re-check">
          <Button
            variant="ghost"
            className="btn-square max-sm:size-11"
            onClick={onRequeue}
            disabled={requeueing}
            aria-label={`Re-check ${title}`}
          >
            <Icon name="replay" className="!text-[18px]" />
          </Button>
        </PortalTooltip>
      )}
      {onDelete && (
        <PortalTooltip content="Remove from InfiniDysk">
          <Button
            variant="ghost"
            className="btn-square hover:text-error max-sm:size-11"
            onClick={onDelete}
            disabled={requeueing}
            aria-label={`Remove ${title}`}
          >
            <Icon name="delete" className="!text-[18px]" />
          </Button>
        </PortalTooltip>
      )}
    </div>
  );

  return (
    <tr className="border-base-content/10">
      <td className="max-w-[320px] py-3 pl-4 align-top md:pl-6 max-[899px]:max-w-none">
        <div className="flex min-w-0 flex-col gap-1">
          <div className="break-all text-sm font-medium leading-snug text-base-content">
            <Truncate>{title}</Truncate>
          </div>
          {item.nzbFileName && item.nzbFileName !== title && (
            <div className="break-all text-xs text-base-content/60">
              <Truncate>{item.nzbFileName}</Truncate>
            </div>
          )}
          <div className="break-all text-xs leading-snug text-base-content/70">
            <Truncate>{item.path}</Truncate>
          </div>
          <div className="mt-1 flex flex-wrap gap-x-3 gap-y-1 min-[900px]:hidden">
            <StatusBadge item={item} />
            {libraryLinkBadge}
            <MetaChip label="When" value={timestamp.relative} />
          </div>
          {item.message && (
            <div className="min-[900px]:hidden">
              <ReasonDetails message={item.message} />
            </div>
          )}
          {actions && <div className="mt-2 min-[900px]:hidden">{actions}</div>}
        </div>
      </td>
      {!combineStatusReason && (
        <td className={desktopCellClass}>
          <StatusBadge item={item} />
        </td>
      )}
      <td className={desktopCellClass}>
        {combineStatusReason && (
          <div className="mb-2 flex flex-wrap gap-2">
            <StatusBadge item={item} />
            {libraryLinkBadge}
          </div>
        )}
        {item.message ? <ReasonDetails message={item.message} /> : "—"}
      </td>
      <td
        className={`${desktopCellClass} whitespace-nowrap font-mono tabular-nums ${actions ? "" : "pr-4 md:pr-6"}`}
      >
        <PortalTooltip content={timestamp.absolute}>
          <time
            dateTime={item.createdAt}
            tabIndex={0}
            className="rounded focus-visible:outline-2 focus-visible:outline-primary"
          >
            {timestamp.relative}
          </time>
        </PortalTooltip>
      </td>
      {actions && (
        <td className="hidden py-2 pr-4 text-right align-top min-[900px]:table-cell md:pr-6">
          {actions}
        </td>
      )}
    </tr>
  );
}

function ReasonDetails({ message }: { message: string }) {
  return (
    <details className="group text-xs leading-relaxed text-base-content/70">
      <summary className="cursor-pointer py-1 font-medium text-base-content focus-visible:outline focus-visible:outline-2 focus-visible:outline-primary">
        Diagnostic details
      </summary>
      <p className="whitespace-pre-wrap break-words pt-1">{message}</p>
    </details>
  );
}

function StatusBadge({ item }: { item: HealthCheckResult }) {
  if (item.result === HealthResultDegraded) {
    return <Badge className="badge-sm badge-warning badge-soft">Degraded</Badge>;
  }
  if (item.repairStatus === RepairActionNeeded) {
    return <Badge className="badge-sm badge-warning badge-soft">Action needed</Badge>;
  }
  const deleted = item.repairStatus === RepairActionDeleted;
  return (
    <Badge className={`badge-sm badge-soft ${deleted ? "badge-error" : "badge-info"}`}>
      {deleted ? "Deleted" : "Repaired"}
    </Badge>
  );
}

function MetaChip({ label, value }: { label: string; value: string }) {
  return (
    <span className="inline-flex max-w-full items-center gap-1.5 text-[11px] text-base-content/55">
      <span className="shrink-0 uppercase tracking-wide text-base-content/40">{label}</span>
      <span className="truncate font-mono tabular-nums text-base-content/70">{value}</span>
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
            New health actions will appear here until health-check retention removes them.
          </p>
        </div>
      </div>
    </div>
  );
}

function basename(path: string) {
  const segments = path.split("/").filter(Boolean);
  return segments.at(-1) ?? path;
}

function formatTimestamp(value: string) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return { relative: "Unknown", absolute: "Unknown" };

  const seconds = Math.max(0, Math.floor((Date.now() - date.getTime()) / 1000));
  const relative =
    seconds < 5
      ? "just now"
      : seconds < 60
        ? `${seconds}s ago`
        : seconds < 3600
          ? `${Math.floor(seconds / 60)}m ago`
          : seconds < 86400
            ? `${Math.floor(seconds / 3600)}h ago`
            : `${Math.floor(seconds / 86400)}d ago`;
  return { relative, absolute: date.toLocaleString() };
}
