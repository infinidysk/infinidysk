import { ActionButton } from "../action-button/action-button";
import { memo, useCallback, useMemo, useState } from "react";
import { ConfirmModal } from "~/components/confirm-modal/confirm-modal";
import type { PresentationQueueSlot } from "../../route";
import type { TriCheckboxState } from "../tri-checkbox/tri-checkbox";
import { PageRow, PageTable } from "../page-table/page-table";
import { PageSection } from "../page-section/page-section";
import { Pagination } from "~/components/pagination/pagination";
import { EmptyQueue } from "../empty-queue/empty-queue";
import { SimpleDropdown } from "~/components/simple-dropdown/simple-dropdown";
import { Button, Tooltip } from "~/components/ui";
import { useIsReadOnly } from "~/auth/authorization";
import type { QueueListParams } from "../../list-params";
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

export type QueueTableProps = {
  queueSlots: PresentationQueueSlot[];
  totalQueueCount: number;
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
  previousQueueSlot?: PresentationQueueSlot | undefined;
  nextQueueSlot?: PresentationQueueSlot | undefined;
  listParams: QueueListParams;
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
  totalQueueCount,
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
  const [clearCategory, setClearCategory] = useState(categories[0] ?? "");
  const [bulkSetCategory, setBulkSetCategory] = useState(categories[0] ?? "");
  const [bulkPriority, setBulkPriority] = useState("0");
  const selectedCount = queueSlots.filter((x) => !!x.isSelected).length;
  const headerCheckboxState: TriCheckboxState =
    selectedCount === 0 ? "none" : selectedCount === queueSlots.length ? "all" : "some";
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
      onIsSelectedChanged(new Set<string>(queueSlots.map((x) => x.nzo_id)), isSelected);
    },
    [queueSlots, onIsSelectedChanged],
  );

  const onRemove = useCallback(() => {
    setIsConfirmingRemoval(true);
  }, [setIsConfirmingRemoval]);

  const onCancelRemoval = useCallback(() => {
    setIsConfirmingRemoval(false);
  }, [setIsConfirmingRemoval]);

  const onConfirmRemoval = useCallback(async () => {
    // immediately remove uploading items
    const uploading_nzo_ids = new Set<string>(
      queueSlots.filter((x) => x.isUploading && !!x.isSelected).map((x) => x.nzo_id),
    );
    onRemoved(uploading_nzo_ids);

    // call backend to remove queued items
    const queued_nzo_ids = new Set<string>(
      queueSlots.filter((x) => !x.isUploading && !!x.isSelected).map((x) => x.nzo_id),
    );
    setIsConfirmingRemoval(false);
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
        // SABnzbd API (`/api?mode=queue&name=delete`) response shape
        const data = (await response.json()) as { status?: boolean };
        if (data.status === true) {
          onRemoved(queued_nzo_ids);
          return;
        }
      }
    } catch {
      // network/API failure: queue unchanged; removing state resets below
    }
    onIsRemovingChanged(queued_nzo_ids, false);
  }, [queueSlots, setIsConfirmingRemoval, onIsRemovingChanged, onRemoved]);

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

  const onMoveSelectedToTop = useCallback(async () => {
    if (selectedMovableIds.length === 0) return;
    const ok = await moveQueueItemsToTop(selectedMovableIds);
    if (!ok) return;
    onMovedToTop(new Set(selectedMovableIds));
    if (!isLive) onPageSelected(1);
  }, [selectedMovableIds, onMovedToTop, isLive, onPageSelected]);

  // view
  const sectionTitle = (
    <div className="flex flex-wrap items-center gap-2.5">
      <h2 className="text-xl font-semibold text-base-content">Queue</h2>
      {!isReadOnly && totalQueueCount > 0 && (
        <>
          <Button variant="secondary" size="xsmall" onClick={() => setIsConfirmingClearAll(true)}>
            Clear all
          </Button>
          {categories.length > 0 && (
            <>
              <SimpleDropdown
                options={categories}
                value={clearCategory}
                onChange={setClearCategory}
              />
              <Button
                variant="secondary"
                size="xsmall"
                onClick={() => setIsConfirmingClearCategory(true)}
              >
                Clear category
              </Button>
            </>
          )}
        </>
      )}
      {!isReadOnly && headerCheckboxState !== "none" && (
        <>
          {selectedPausableIds.length > 0 && (
            <Tooltip content="Pause selected">
              <ActionButton type="pause" onClick={() => void onPauseSelected()} />
            </Tooltip>
          )}
          {selectedResumableIds.length > 0 && (
            <Tooltip content="Resume selected">
              <ActionButton type="resume" onClick={() => void onResumeSelected()} />
            </Tooltip>
          )}
          {selectedMovableIds.length > 0 && (
            <Tooltip content="Move selected to top of queue">
              <ActionButton type="move-top" onClick={() => void onMoveSelectedToTop()} />
            </Tooltip>
          )}
          {selectedMovableIds.length > 0 && categories.length > 0 && (
            <>
              <SimpleDropdown
                options={categories}
                value={bulkSetCategory}
                onChange={setBulkSetCategory}
              />
              <Button
                variant="secondary"
                size="xsmall"
                onClick={() => void onSetCategorySelected()}
              >
                Set category
              </Button>
            </>
          )}
          {selectedMovableIds.length > 0 && (
            <>
              <SimpleDropdown
                options={["-1", "0", "1", "2"]}
                value={bulkPriority}
                onChange={setBulkPriority}
              />
              <Button
                variant="secondary"
                size="xsmall"
                onClick={() => void onSetPrioritySelected(bulkPriority)}
              >
                Set priority
              </Button>
            </>
          )}
          <ActionButton type="delete" onClick={onRemove} />
        </>
      )}
    </div>
  );

  const footer =
    totalQueueCount > 0 ? (
      <div className="flex flex-col items-center gap-2 text-xs text-base-content/60">
        {!isLive && <span>Live updates pause on older pages. Go to page 1 for live.</span>}
        <Pagination
          pageNumber={pageNumber}
          totalPages={totalPages}
          totalCount={totalQueueCount}
          pageSize={pageSize}
          pageSizeOptions={pageSizeOptions}
          onPageSelected={onPageSelected}
          onPageSizeSelected={onPageSizeSelected}
        />
      </div>
    ) : undefined;

  return (
    <PageSection
      title={sectionTitle}
      {...(totalQueueCount > 0 ? { badgeText: String(totalQueueCount) } : {})}
    >
      <ListToolbar
        label="Queue"
        query={searchDraft}
        category={listParams.category}
        status={listParams.status}
        sort={sortValue(listParams)}
        categories={categories}
        statuses={[
          { value: "Downloading", label: "Downloading" },
          { value: "Queued", label: "Queued" },
          { value: "Paused", label: "Paused" },
        ]}
        sorts={[
          { value: "name:asc", label: "Name A–Z" },
          { value: "name:desc", label: "Name Z–A" },
          { value: "size:desc", label: "Size largest" },
          { value: "size:asc", label: "Size smallest" },
          { value: "status:asc", label: "Status" },
          { value: "category:asc", label: "Category" },
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
      {queueSlots?.length == 0 ? (
        <EmptyQueue />
      ) : (
        <PageTable
          headerCheckboxState={headerCheckboxState}
          onHeaderCheckboxChange={onSelectAll}
          footer={footer}
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
        </PageTable>
      )}

      <ConfirmModal
        show={isConfirmingRemoval}
        title="Remove From Queue?"
        message={`${selectedCount} item(s) will be removed`}
        onConfirm={() => void onConfirmRemoval()}
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
    </PageSection>
  );
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
              <div className="flex items-center justify-center gap-1">
                {!slot.isUploading && (
                  <>
                    <Tooltip content="Move up">
                      <ActionButton
                        type="move-up"
                        disabled={
                          !!slot.isRemoving ||
                          isMoving ||
                          !previousSlot ||
                          previousSlot.isUploading ||
                          previousSlot.status === "Downloading"
                        }
                        onClick={() => void moveRelative(previousSlot)}
                      />
                    </Tooltip>
                    <Tooltip content="Move down">
                      <ActionButton
                        type="move-down"
                        disabled={
                          !!slot.isRemoving ||
                          isMoving ||
                          !nextSlot ||
                          nextSlot.isUploading ||
                          nextSlot.status === "Downloading"
                        }
                        onClick={() => void moveRelative(nextSlot)}
                      />
                    </Tooltip>
                  </>
                )}
                {!slot.isUploading && (
                  <Tooltip content="Move to top">
                    <ActionButton
                      type="move-top"
                      disabled={!!slot.isRemoving || isMoving}
                      onClick={() => void onMoveToTop()}
                    />
                  </Tooltip>
                )}
                <ActionButton
                  type="delete"
                  disabled={!!slot.isRemoving || isActivelyUploading}
                  onClick={onRemove}
                />
              </div>
            )
          }
          onRowSelectionChanged={(isSelected) => onIsSelectedChanged(slot.nzo_id, isSelected)}
          selectable={!isReadOnly}
          error={slot.error}
          indexer={slot.indexer}
          providers={slot.providers}
        />
        <ConfirmModal
          show={isConfirmingRemoval}
          title="Remove From Queue?"
          message={slot.filename}
          onConfirm={() => void onConfirmRemoval()}
          onCancel={onCancelRemoval}
        />
      </>
    );
  },
);
