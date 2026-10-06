import { ActionButton, ActionSpacer } from "../action-button/action-button";
import { memo, useCallback, useMemo, useState } from "react";
import { ConfirmModal } from "~/components/confirm-modal/confirm-modal";
import type { PresentationHistorySlot, PresentationQueueSlot } from "../../route";
import type { TriCheckboxState } from "../tri-checkbox/tri-checkbox";
import { PageGroupRow, PageRow, PageTable } from "../page-table/page-table";
import { PageSection } from "../page-section/page-section";
import { Pagination } from "~/components/pagination/pagination";
import { EmptyQueue } from "../empty-queue/empty-queue";
import { Button, Icon, Select } from "~/components/ui";
import { useIsReadOnly } from "~/auth/authorization";
import type { JobsListParams } from "../../list-params";
import { sortValue } from "../../list-params";
import { ListToolbar } from "../list-toolbar/list-toolbar";
import {
  canPauseQueueSlot,
  canResumeQueueSlot,
  postClearQueue,
  postQueueCategory,
  postQueuePause,
  postQueuePriority,
  postQueueResume,
} from "./queue-bulk-actions";
import { HistoryRow } from "../history-table/history-table";
import { canRetryHistorySlot, retryHistoryItems } from "../history-table/history-retry";

export type QueueTableProps = {
  queueSlots: PresentationQueueSlot[];
  historySlots: PresentationHistorySlot[];
  totalQueueCount: number;
  totalHistoryCount: number;
  pageNumber: number;
  pageSize: number;
  pageSizeOptions: readonly number[];
  totalPages: number;
  isLive: boolean;
  onPageSelected: (page: number) => void;
  onPageSizeSelected: (pageSize: number) => void;
  categories: string[];
  onIsSelectedChanged: (nzo_ids: Set<string>, isSelected: boolean) => void;
  onIsRemovingChanged: (nzo_ids: Set<string>, isRemoving: boolean) => void;
  onRemoved: (nzo_ids: Set<string>) => void;
  onMovedToTop: (nzo_ids: Set<string>) => void;
  onHistoryIsSelectedChanged: (nzo_ids: Set<string>, isSelected: boolean) => void;
  onHistoryIsRemovingChanged: (nzo_ids: Set<string>, isRemoving: boolean) => void;
  onHistoryRemoved: (nzo_ids: Set<string>) => void;
  previousQueueSlot?: PresentationQueueSlot | undefined;
  nextQueueSlot?: PresentationQueueSlot | undefined;
  listParams: JobsListParams;
  searchDraft: string;
  onSearchDraftChange: (value: string) => void;
  onFilterChange: (key: string, value: string) => void;
  onClearFilters: () => void;
  onSort: (field: string) => void;
};

async function moveQueueItemsToTop(nzoIds: string[]): Promise<boolean> {
  if (nzoIds.length === 0) return false;
  try {
    const url = `/api?mode=queue&name=move&value2=0`;
    const response = await fetch(url, {
      method: "POST",
      headers: {
        "Content-Type": "application/json;charset=UTF-8",
      },
      body: JSON.stringify({ nzo_ids: nzoIds }),
    });
    if (!response.ok) return false;
    // SABnzbd API (`/api?mode=queue&name=move`) response shape
    const data = (await response.json()) as { status?: boolean };
    return data.status === true;
  } catch {
    return false;
  }
}

async function switchQueueItem(sourceId: string, targetId: string): Promise<boolean> {
  try {
    const response = await fetch(
      `/api?mode=switch&value=${encodeURIComponent(sourceId)}&value2=${encodeURIComponent(targetId)}`,
      { method: "POST" },
    );
    if (!response.ok) return false;
    const data = (await response.json()) as { result?: { position?: number } };
    return (data.result?.position ?? -1) >= 0;
  } catch {
    return false;
  }
}

export function QueueTable({
  queueSlots,
  historySlots,
  totalQueueCount,
  totalHistoryCount,
  pageNumber,
  pageSize,
  pageSizeOptions,
  totalPages,
  isLive,
  onPageSelected,
  onPageSizeSelected,
  categories,
  onIsSelectedChanged,
  onIsRemovingChanged,
  onRemoved,
  onMovedToTop,
  onHistoryIsSelectedChanged,
  onHistoryIsRemovingChanged,
  onHistoryRemoved,
  previousQueueSlot,
  nextQueueSlot,
  listParams,
  searchDraft,
  onSearchDraftChange,
  onFilterChange,
  onClearFilters,
  onSort,
}: QueueTableProps) {
  const isReadOnly = useIsReadOnly();
  const [isConfirmingRemoval, setIsConfirmingRemoval] = useState(false);
  const [isConfirmingClearAll, setIsConfirmingClearAll] = useState(false);
  const [isConfirmingClearCategory, setIsConfirmingClearCategory] = useState(false);
  const [isConfirmingClearFailed, setIsConfirmingClearFailed] = useState(false);
  const [isConfirmingClearAllHistory, setIsConfirmingClearAllHistory] = useState(false);
  const [clearCategory, setClearCategory] = useState(categories[0] ?? "");
  const [bulkSetCategory, setBulkSetCategory] = useState(categories[0] ?? "");
  const [bulkPriority, setBulkPriority] = useState("0");
  const [bulkRetryError, setBulkRetryError] = useState<string | null>(null);
  const selectedQueueCount = queueSlots.filter((x) => !!x.isSelected).length;
  const selectedHistoryCount = historySlots.filter((x) => !!x.isSelected).length;
  const selectedCount = selectedQueueCount + selectedHistoryCount;
  const visibleCount = queueSlots.length + historySlots.length;
  const headerCheckboxState: TriCheckboxState =
    selectedCount === 0
      ? "none"
      : selectedCount === visibleCount && visibleCount > 0
        ? "all"
        : "some";
  const selectedMovableIds = useMemo(
    () => queueSlots.filter((x) => !!x.isSelected && !x.isUploading).map((x) => x.nzo_id),
    [queueSlots],
  );
  const selectedPausableIds = useMemo(
    () => queueSlots.filter((x) => !!x.isSelected && canPauseQueueSlot(x)).map((x) => x.nzo_id),
    [queueSlots],
  );
  const selectedResumableIds = useMemo(
    () => queueSlots.filter((x) => !!x.isSelected && canResumeQueueSlot(x)).map((x) => x.nzo_id),
    [queueSlots],
  );
  const selectedRetryableIds = useMemo(
    () => historySlots.filter((x) => !!x.isSelected && canRetryHistorySlot(x)).map((x) => x.nzo_id),
    [historySlots],
  );

  // row events
  const onRowIsSelectedChanged = useCallback(
    (id: string, isSelected: boolean) => {
      onIsSelectedChanged(new Set<string>([id]), isSelected);
    },
    [onIsSelectedChanged],
  );

  const onRowIsRemovingChanged = useCallback(
    (id: string, isRemoving: boolean) => {
      onIsRemovingChanged(new Set<string>([id]), isRemoving);
    },
    [onIsRemovingChanged],
  );

  const onRowRemoved = useCallback(
    (id: string) => {
      onRemoved(new Set([id]));
    },
    [onRemoved],
  );

  const onRowMovedToTop = useCallback(
    (id: string) => {
      onMovedToTop(new Set([id]));
      if (!isLive) onPageSelected(1);
    },
    [onMovedToTop, isLive, onPageSelected],
  );

  // table events
  const onSelectAll = useCallback(
    (isSelected: boolean) => {
      if (queueSlots.length > 0) {
        onIsSelectedChanged(new Set<string>(queueSlots.map((x) => x.nzo_id)), isSelected);
      }
      if (historySlots.length > 0) {
        onHistoryIsSelectedChanged(new Set<string>(historySlots.map((x) => x.nzo_id)), isSelected);
      }
    },
    [queueSlots, historySlots, onIsSelectedChanged, onHistoryIsSelectedChanged],
  );

  const onRemove = useCallback(() => {
    setIsConfirmingRemoval(true);
  }, [setIsConfirmingRemoval]);

  const onCancelRemoval = useCallback(() => {
    setIsConfirmingRemoval(false);
  }, [setIsConfirmingRemoval]);

  const onConfirmRemoval = useCallback(
    async (deleteCompletedFiles?: boolean) => {
      const uploading_nzo_ids = new Set<string>(
        queueSlots.filter((x) => x.isUploading && !!x.isSelected).map((x) => x.nzo_id),
      );
      onRemoved(uploading_nzo_ids);

      const queued_nzo_ids = new Set<string>(
        queueSlots.filter((x) => !x.isUploading && !!x.isSelected).map((x) => x.nzo_id),
      );
      const history_nzo_ids = new Set<string>(
        historySlots.filter((x) => !!x.isSelected).map((x) => x.nzo_id),
      );
      setIsConfirmingRemoval(false);

      if (queued_nzo_ids.size > 0) {
        onIsRemovingChanged(queued_nzo_ids, true);
        try {
          const url = `/api?mode=queue&name=delete`;
          const response = await fetch(url, {
            method: "POST",
            headers: {
              "Content-Type": "application/json;charset=UTF-8",
            },
            body: JSON.stringify({ nzo_ids: Array.from(queued_nzo_ids) }),
          });
          if (response.ok) {
            const data = (await response.json()) as { status?: boolean };
            if (data.status === true) {
              onRemoved(queued_nzo_ids);
            } else {
              onIsRemovingChanged(queued_nzo_ids, false);
            }
          } else {
            onIsRemovingChanged(queued_nzo_ids, false);
          }
        } catch {
          onIsRemovingChanged(queued_nzo_ids, false);
        }
      }

      if (history_nzo_ids.size > 0) {
        onHistoryIsRemovingChanged(history_nzo_ids, true);
        try {
          const url = `/api?mode=history&name=delete&del_completed_files=${deleteCompletedFiles ? 1 : 0}`;
          const response = await fetch(url, {
            method: "POST",
            headers: {
              "Content-Type": "application/json;charset=UTF-8",
            },
            body: JSON.stringify({ nzo_ids: Array.from(history_nzo_ids) }),
          });
          if (response.ok) {
            const data = (await response.json()) as { status?: boolean };
            if (data.status === true) {
              onHistoryRemoved(history_nzo_ids);
            } else {
              onHistoryIsRemovingChanged(history_nzo_ids, false);
            }
          } else {
            onHistoryIsRemovingChanged(history_nzo_ids, false);
          }
        } catch {
          onHistoryIsRemovingChanged(history_nzo_ids, false);
        }
      }
    },
    [
      queueSlots,
      historySlots,
      onIsRemovingChanged,
      onRemoved,
      onHistoryIsRemovingChanged,
      onHistoryRemoved,
    ],
  );

  const onPauseSelected = useCallback(async () => {
    if (selectedPausableIds.length === 0) return;
    await postQueuePause(selectedPausableIds);
  }, [selectedPausableIds]);

  const onResumeSelected = useCallback(async () => {
    if (selectedResumableIds.length === 0) return;
    await postQueueResume(selectedResumableIds);
  }, [selectedResumableIds]);

  const onSetPrioritySelected = useCallback(
    async (priority: string) => {
      if (selectedMovableIds.length === 0) return;
      await postQueuePriority(selectedMovableIds, priority);
    },
    [selectedMovableIds],
  );

  const onSetCategorySelected = useCallback(async () => {
    if (selectedMovableIds.length === 0 || !bulkSetCategory) return;
    await postQueueCategory(selectedMovableIds, bulkSetCategory);
  }, [selectedMovableIds, bulkSetCategory]);

  const onConfirmClearAll = useCallback(async () => {
    setIsConfirmingClearAll(false);
    await postClearQueue();
  }, []);

  const onConfirmClearCategory = useCallback(async () => {
    setIsConfirmingClearCategory(false);
    if (!clearCategory) return;
    await postClearQueue(clearCategory);
  }, [clearCategory]);

  const onBulkRetry = useCallback(async () => {
    if (selectedRetryableIds.length === 0) return;
    setBulkRetryError(null);
    const result = await retryHistoryItems(selectedRetryableIds);
    if (result.succeeded.length > 0) {
      onHistoryRemoved(new Set(result.succeeded));
    }
    if (result.failed.length > 0) {
      setBulkRetryError(result.failed[0]?.error ?? "Failed to retry history items.");
    }
  }, [selectedRetryableIds, onHistoryRemoved]);

  const onConfirmClearFailed = useCallback(async (deleteCompletedFiles?: boolean) => {
    setIsConfirmingClearFailed(false);
    try {
      const url = `/api?mode=history&name=delete&value=failed&del_completed_files=${deleteCompletedFiles ? 1 : 0}`;
      await fetch(url, { method: "POST" });
    } catch {
      /* best effort */
    }
  }, []);

  const onConfirmClearAllHistory = useCallback(async (deleteCompletedFiles?: boolean) => {
    setIsConfirmingClearAllHistory(false);
    try {
      const url = `/api?mode=history&name=delete&value=all&del_completed_files=${deleteCompletedFiles ? 1 : 0}`;
      await fetch(url, { method: "POST" });
    } catch {
      /* best effort */
    }
  }, []);

  const onMoveSelectedToTop = useCallback(async () => {
    if (selectedMovableIds.length === 0) return;
    const ok = await moveQueueItemsToTop(selectedMovableIds);
    if (!ok) return;
    onMovedToTop(new Set(selectedMovableIds));
    if (!isLive) onPageSelected(1);
  }, [selectedMovableIds, onMovedToTop, isLive, onPageSelected]);

  // view
  const closeMenu = (e: React.MouseEvent<HTMLElement>) =>
    e.currentTarget.closest("details")?.removeAttribute("open");
  const sectionTitle = (
    <div className="flex w-full flex-col gap-2">
      <div className="flex flex-wrap items-center gap-2">
        <span className="text-xs text-base-content/60 tabular-nums">
          {totalQueueCount} active · {totalHistoryCount} in history
        </span>
        {!isReadOnly && (totalQueueCount > 0 || totalHistoryCount > 0) && (
          <details className="dropdown dropdown-end ml-auto">
            <summary className="btn btn-ghost btn-sm list-none max-sm:min-h-11">
              <Icon name="cleaning_services" className="!text-[18px]" />
              Clean up
              <Icon name="expand_more" className="!text-[18px]" />
            </summary>
            <ul className="menu dropdown-content z-20 mt-2 max-h-80 w-60 flex-nowrap overflow-y-auto rounded-box border border-base-content/10 bg-base-200 p-2 shadow-lg">
              {totalQueueCount > 0 && (
                <>
                  <li className="menu-title">Queue</li>
                  <li>
                    <button
                      type="button"
                      className="text-error"
                      onClick={(e) => {
                        closeMenu(e);
                        setIsConfirmingClearAll(true);
                      }}
                    >
                      <Icon name="delete_sweep" className="!text-[18px]" />
                      Clear entire queue
                    </button>
                  </li>
                  {categories.map((category) => (
                    <li key={category}>
                      <button
                        type="button"
                        onClick={(e) => {
                          closeMenu(e);
                          setClearCategory(category);
                          setIsConfirmingClearCategory(true);
                        }}
                      >
                        <Icon name="label_off" className="!text-[18px]" />
                        <span className="truncate">Clear “{category}” jobs</span>
                      </button>
                    </li>
                  ))}
                </>
              )}
              {totalHistoryCount > 0 && (
                <>
                  <li className="menu-title">History</li>
                  <li>
                    <button
                      type="button"
                      onClick={(e) => {
                        closeMenu(e);
                        setIsConfirmingClearFailed(true);
                      }}
                    >
                      <Icon name="error" className="!text-[18px]" />
                      Clear failed jobs
                    </button>
                  </li>
                  <li>
                    <button
                      type="button"
                      className="text-error"
                      onClick={(e) => {
                        closeMenu(e);
                        setIsConfirmingClearAllHistory(true);
                      }}
                    >
                      <Icon name="delete_sweep" className="!text-[18px]" />
                      Clear all history
                    </button>
                  </li>
                </>
              )}
            </ul>
          </details>
        )}
      </div>
      {!isReadOnly && selectedCount > 0 && (
        <div
          role="toolbar"
          aria-label="Selected jobs"
          className="flex flex-wrap items-center gap-2 rounded-box border border-primary/30 bg-primary/5 px-3 py-2"
        >
          <span className="text-sm font-medium tabular-nums">{selectedCount} selected</span>
          <Button variant="ghost" onClick={() => onSelectAll(false)}>
            Clear selection
          </Button>
          <div className="flex flex-wrap items-center gap-2 sm:ml-auto">
            {selectedPausableIds.length > 0 && (
              <Button onClick={() => void onPauseSelected()}>
                <Icon name="pause" className="!text-[18px]" />
                Pause
              </Button>
            )}
            {selectedResumableIds.length > 0 && (
              <Button onClick={() => void onResumeSelected()}>
                <Icon name="play_arrow" className="!text-[18px]" />
                Resume
              </Button>
            )}
            {selectedMovableIds.length > 0 && (
              <Button onClick={() => void onMoveSelectedToTop()}>
                <Icon name="vertical_align_top" className="!text-[18px]" />
                Move to top
              </Button>
            )}
            {selectedRetryableIds.length > 0 && (
              <Button onClick={() => void onBulkRetry()}>
                <Icon name="refresh" className="!text-[18px]" />
                Retry
              </Button>
            )}
            {selectedMovableIds.length > 0 && categories.length > 0 && (
              <div className="join">
                <Select
                  className="select-sm join-item w-auto max-sm:min-h-11"
                  aria-label="Category for selected jobs"
                  value={bulkSetCategory}
                  onChange={(e) => setBulkSetCategory(e.target.value)}
                >
                  {categories.map((category) => (
                    <option key={category} value={category}>
                      {category}
                    </option>
                  ))}
                </Select>
                <Button className="join-item" onClick={() => void onSetCategorySelected()}>
                  Set category
                </Button>
              </div>
            )}
            {selectedMovableIds.length > 0 && (
              <div className="join">
                <Select
                  className="select-sm join-item w-auto max-sm:min-h-11"
                  aria-label="Priority for selected jobs"
                  value={bulkPriority}
                  onChange={(e) => setBulkPriority(e.target.value)}
                >
                  <option value="-1">Low</option>
                  <option value="0">Normal</option>
                  <option value="1">High</option>
                  <option value="2">Force</option>
                </Select>
                <Button
                  className="join-item"
                  onClick={() => void onSetPrioritySelected(bulkPriority)}
                >
                  Set priority
                </Button>
              </div>
            )}
            <Button className="btn-error btn-soft" onClick={onRemove}>
              <Icon name="delete" className="!text-[18px]" />
              Remove
            </Button>
          </div>
          {bulkRetryError && (
            <span role="alert" className="basis-full text-xs text-error">
              {bulkRetryError}
            </span>
          )}
        </div>
      )}
    </div>
  );

  const combinedTotal = totalQueueCount + totalHistoryCount;
  const footer =
    combinedTotal > 0 ? (
      <div className="flex flex-col items-center gap-2 text-xs text-base-content/60">
        {!isLive && <span>Live updates pause on older pages. Go to page 1 for live.</span>}
        <Pagination
          pageNumber={pageNumber}
          totalPages={totalPages}
          totalCount={combinedTotal}
          pageSize={pageSize}
          pageSizeOptions={pageSizeOptions}
          onPageSelected={onPageSelected}
          onPageSizeSelected={onPageSizeSelected}
        />
      </div>
    ) : undefined;

  const isEmpty = queueSlots.length === 0 && historySlots.length === 0;

  return (
    <PageSection title={sectionTitle}>
      <ListToolbar
        label="queue"
        query={searchDraft}
        category={listParams.category}
        status={listParams.status}
        sort={sortValue(listParams)}
        categories={categories}
        statuses={[
          { value: "Downloading", label: "Downloading" },
          { value: "Queued", label: "Queued" },
          { value: "Paused", label: "Paused" },
          { value: "Completed", label: "Completed" },
          { value: "Failed", label: "Failed" },
        ]}
        sorts={[
          { value: "name:asc", label: "Name A–Z" },
          { value: "name:desc", label: "Name Z–A" },
          { value: "size:desc", label: "Size largest" },
          { value: "size:asc", label: "Size smallest" },
          { value: "status:asc", label: "Status" },
          { value: "category:asc", label: "Category" },
          { value: "completed:desc", label: "Newest first" },
          { value: "completed:asc", label: "Oldest first" },
        ]}
        isFiltered={
          !!(listParams.query || listParams.category || listParams.status || listParams.sort)
        }
        onQueryChange={onSearchDraftChange}
        onCategoryChange={(value) => onFilterChange("qcat", value)}
        onStatusChange={(value) => onFilterChange("qstatus", value)}
        onSortChange={(value) => onFilterChange("qsort", value)}
        onClear={onClearFilters}
      />
      {isEmpty ? (
        <EmptyQueue
          onClearFilters={
            listParams.query || listParams.category || listParams.status
              ? onClearFilters
              : undefined
          }
        />
      ) : (
        <PageTable
          headerCheckboxState={headerCheckboxState}
          onHeaderCheckboxChange={onSelectAll}
          footer={footer}
          showCompleted
          selectable={!isReadOnly}
          sort={listParams.sort ?? undefined}
          direction={listParams.direction}
          onSort={onSort}
        >
          {queueSlots.map((slot, index) => (
            <QueueRow
              key={slot.nzo_id}
              slot={slot}
              previousSlot={index > 0 ? queueSlots[index - 1] : previousQueueSlot}
              nextSlot={index < queueSlots.length - 1 ? queueSlots[index + 1] : nextQueueSlot}
              onIsSelectedChanged={onRowIsSelectedChanged}
              onIsRemovingChanged={onRowIsRemovingChanged}
              onRemoved={onRowRemoved}
              onMovedToTop={onRowMovedToTop}
            />
          ))}
          {queueSlots.length > 0 && historySlots.length > 0 && (
            <PageGroupRow label="History" count={totalHistoryCount} showCompleted />
          )}
          {historySlots.map((slot) => (
            <HistoryRow
              key={slot.nzo_id}
              slot={slot}
              onIsSelectedChanged={(id, isSelected) =>
                onHistoryIsSelectedChanged(new Set<string>([id]), isSelected)
              }
              onIsRemovingChanged={(id, isRemoving) =>
                onHistoryIsRemovingChanged(new Set<string>([id]), isRemoving)
              }
              onRemoved={(id) => onHistoryRemoved(new Set([id]))}
            />
          ))}
        </PageTable>
      )}

      <ConfirmModal
        show={isConfirmingRemoval}
        title="Remove items?"
        message={removalMessage(selectedQueueCount, selectedHistoryCount)}
        {...(selectedHistoryCount > 0 ? { checkboxMessage: "Delete mounted files" } : {})}
        onConfirm={(isChecked) => void onConfirmRemoval(isChecked)}
        onCancel={onCancelRemoval}
      />
      <ConfirmModal
        show={isConfirmingClearAll}
        title="Clear entire queue?"
        message="All queued items will be removed. In-progress downloads will be cancelled."
        onConfirm={() => void onConfirmClearAll()}
        onCancel={() => setIsConfirmingClearAll(false)}
      />
      <ConfirmModal
        show={isConfirmingClearCategory}
        title="Clear category?"
        message={`All items in category "${clearCategory}" will be removed.`}
        onConfirm={() => void onConfirmClearCategory()}
        onCancel={() => setIsConfirmingClearCategory(false)}
      />
      <ConfirmModal
        show={isConfirmingClearFailed}
        title="Clear failed history?"
        message="All failed history items will be removed."
        checkboxMessage="Delete mounted files"
        onConfirm={(isChecked) => void onConfirmClearFailed(isChecked)}
        onCancel={() => setIsConfirmingClearFailed(false)}
      />
      <ConfirmModal
        show={isConfirmingClearAllHistory}
        title="Clear all history?"
        message="All history items will be removed."
        checkboxMessage="Delete mounted files"
        onConfirm={(isChecked) => void onConfirmClearAllHistory(isChecked)}
        onCancel={() => setIsConfirmingClearAllHistory(false)}
      />
    </PageSection>
  );
}

function removalMessage(queueCount: number, historyCount: number) {
  const parts: string[] = [];
  const plural = (n: number, noun: string) => `${n} ${noun}${n === 1 ? "" : "s"}`;
  if (queueCount > 0) parts.push(plural(queueCount, "queued job"));
  if (historyCount > 0) parts.push(plural(historyCount, "history job"));
  if (parts.length === 0) return "Selected items will be removed.";
  return `${parts.join(" and ")} will be removed.`;
}

type QueueRowProps = {
  slot: PresentationQueueSlot;
  onIsSelectedChanged: (nzo_id: string, isSelected: boolean) => void;
  onIsRemovingChanged: (nzo_id: string, isRemoving: boolean) => void;
  onRemoved: (nzo_id: string) => void;
  onMovedToTop: (nzo_id: string) => void;
  previousSlot?: PresentationQueueSlot | undefined;
  nextSlot?: PresentationQueueSlot | undefined;
};

export const QueueRow = memo(
  ({
    slot,
    previousSlot,
    nextSlot,
    onIsSelectedChanged,
    onIsRemovingChanged,
    onRemoved,
    onMovedToTop,
  }: QueueRowProps) => {
    const isReadOnly = useIsReadOnly();
    // state
    const [isConfirmingRemoval, setIsConfirmingRemoval] = useState(false);
    const [isMoving, setIsMoving] = useState(false);
    const isActivelyUploading = !!(slot.isUploading && slot.status == "uploading");
    const canSwapWith = (target?: PresentationQueueSlot) =>
      !slot.isUploading && !!target && !target.isUploading && target.status !== "Downloading";
    const canMoveUp = canSwapWith(previousSlot);
    const canMoveDown = canSwapWith(nextSlot);

    // events
    const onRemove = useCallback(() => {
      // immediately remove uploading items, without need of confirmation.
      if (slot.isUploading) {
        onRemoved(slot.nzo_id);
        return;
      }

      setIsConfirmingRemoval(true);
    }, [slot.isUploading, slot.nzo_id, onRemoved, setIsConfirmingRemoval]);

    const onCancelRemoval = useCallback(() => {
      setIsConfirmingRemoval(false);
    }, [setIsConfirmingRemoval]);

    const onConfirmRemoval = useCallback(async () => {
      if (slot.isUploading) return;
      setIsConfirmingRemoval(false);
      onIsRemovingChanged(slot.nzo_id, true);
      try {
        const url = "/api?mode=queue&name=delete" + `&value=${encodeURIComponent(slot.nzo_id)}`;
        const response = await fetch(url);
        if (response.ok) {
          // SABnzbd API (`/api?mode=queue&name=delete`) response shape
          const data = (await response.json()) as { status?: boolean };
          if (data.status === true) {
            onRemoved(slot.nzo_id);
            return;
          }
        }
      } catch {
        // network/API failure: queue unchanged; removing state resets below
      }
      onIsRemovingChanged(slot.nzo_id, false);
    }, [slot.nzo_id, slot.isUploading, setIsConfirmingRemoval, onIsRemovingChanged, onRemoved]);

    const onMoveToTop = useCallback(async () => {
      if (slot.isUploading || isMoving) return;
      setIsMoving(true);
      try {
        const ok = await moveQueueItemsToTop([slot.nzo_id]);
        if (ok) onMovedToTop(slot.nzo_id);
      } finally {
        setIsMoving(false);
      }
    }, [slot.isUploading, slot.nzo_id, isMoving, onMovedToTop]);

    const moveRelative = useCallback(
      async (target?: PresentationQueueSlot) => {
        if (
          !target ||
          slot.isUploading ||
          isMoving ||
          target.isUploading ||
          target.status === "Downloading"
        )
          return;
        setIsMoving(true);
        try {
          await switchQueueItem(slot.nzo_id, target.nzo_id);
        } finally {
          setIsMoving(false);
        }
      },
      [slot.isUploading, slot.nzo_id, isMoving],
    );

    // view
    return (
      <>
        <PageRow
          isUploading={!!slot.isUploading}
          isSelected={!!slot.isSelected}
          isRemoving={!!slot.isRemoving}
          name={slot.filename}
          category={slot.cat}
          status={slot.status}
          percentage={slot.true_percentage}
          fileSizeBytes={Number(slot.mb) * 1024 * 1024}
          actions={
            isReadOnly ? null : (
              <>
                {canMoveUp ? (
                  <ActionButton
                    type="move-up"
                    subject={slot.filename}
                    disabled={!!slot.isRemoving || isMoving}
                    onClick={() => void moveRelative(previousSlot)}
                  />
                ) : (
                  <ActionSpacer />
                )}
                {canMoveDown ? (
                  <ActionButton
                    type="move-down"
                    subject={slot.filename}
                    disabled={!!slot.isRemoving || isMoving}
                    onClick={() => void moveRelative(nextSlot)}
                  />
                ) : (
                  <ActionSpacer />
                )}
                {!slot.isUploading && previousSlot ? (
                  <ActionButton
                    type="move-top"
                    subject={slot.filename}
                    disabled={!!slot.isRemoving || isMoving}
                    onClick={() => void onMoveToTop()}
                  />
                ) : (
                  <ActionSpacer />
                )}
                <ActionButton
                  type="delete"
                  subject={slot.filename}
                  disabled={!!slot.isRemoving || isActivelyUploading}
                  onClick={onRemove}
                />
              </>
            )
          }
          onRowSelectionChanged={(isSelected) => onIsSelectedChanged(slot.nzo_id, isSelected)}
          selectable={!isReadOnly}
          error={slot.error}
          indexer={slot.indexer}
          providers={slot.providers}
          showCompleted
        />
        <ConfirmModal
          show={isConfirmingRemoval}
          title="Remove from queue?"
          message={slot.filename}
          onConfirm={() => void onConfirmRemoval()}
          onCancel={onCancelRemoval}
        />
      </>
    );
  },
);
