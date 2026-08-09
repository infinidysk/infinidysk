import { ActionButton } from "../action-button/action-button"
import { useCallback, useState } from "react"
import { ConfirmModal } from "~/components/confirm-modal/confirm-modal"
import { Link } from "react-router"
import { type TriCheckboxState } from "../tri-checkbox/tri-checkbox"
import type { PresentationHistorySlot } from "../../route"
import { getExploreContentLink } from "~/utils/path"
import { PageRow, PageTable } from "../page-table/page-table"
import { PageSection } from "../page-section/page-section"
import { Pagination } from "../pagination/pagination"
import { DropdownOptions } from "~/routes/explore/dropdown-options/dropdown-options"
import { ExportNzb, Remove } from "~/routes/explore/item-menu/item-menu"
import { canRetryHistorySlot, retryHistoryItem, shouldAcceptRetryClick } from "./history-retry"
import { useIsReadOnly } from "~/auth/authorization"

export type HistoryTableProps = {
    historySlots: PresentationHistorySlot[],
    totalHistoryCount: number,
    pageNumber: number,
    pageSize: number,
    pageSizeOptions: readonly number[],
    totalPages: number,
    isLive: boolean,
    onPageSelected: (page: number) => void,
    onPageSizeSelected: (pageSize: number) => void,
    onIsSelectedChanged: (nzo_ids: Set<string>, isSelected: boolean) => void,
    onIsRemovingChanged: (nzo_ids: Set<string>, isRemoving: boolean) => void,
    onRemoved: (nzo_ids: Set<string>) => void,
}

export function HistoryTable({
    historySlots,
    totalHistoryCount,
    pageNumber,
    pageSize,
    pageSizeOptions,
    totalPages,
    isLive,
    onPageSelected,
    onPageSizeSelected,
    onIsSelectedChanged,
    onIsRemovingChanged,
    onRemoved,
}: HistoryTableProps) {
    const isReadOnly = useIsReadOnly();
    const [isConfirmingRemoval, setIsConfirmingRemoval] = useState(false);
    const selectedCount = historySlots.filter(x => !!x.isSelected).length;
    const headerCheckboxState: TriCheckboxState = selectedCount === 0 ? 'none' : selectedCount === historySlots.length ? 'all' : 'some';

    const onSelectAll = useCallback((isSelected: boolean) => {
        onIsSelectedChanged(new Set<string>(historySlots.map(x => x.nzo_id)), isSelected);
    }, [historySlots, onIsSelectedChanged]);

    const onRemove = useCallback(() => {
        setIsConfirmingRemoval(true);
    }, [setIsConfirmingRemoval]);

    const onCancelRemoval = useCallback(() => {
        setIsConfirmingRemoval(false);
    }, [setIsConfirmingRemoval]);

    const onConfirmRemoval = useCallback(async (deleteCompletedFiles?: boolean) => {
        const nzo_ids = new Set<string>(historySlots.filter(x => !!x.isSelected).map(x => x.nzo_id));
        setIsConfirmingRemoval(false);
        onIsRemovingChanged(nzo_ids, true);
        try {
            const url = `/api?mode=history&name=delete&del_completed_files=${deleteCompletedFiles ? 1 : 0}`;
            const response = await fetch(url, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json;charset=UTF-8',
                },
                body: JSON.stringify({ nzo_ids: Array.from(nzo_ids) }),
            });
            if (response.ok) {
                const data = await response.json();
                if (data.status === true) {
                    onRemoved(nzo_ids);
                    return;
                }
            }
        } catch { }
        onIsRemovingChanged(nzo_ids, false);
    }, [historySlots, setIsConfirmingRemoval, onIsRemovingChanged, onRemoved]);

    const sectionTitle = (
        <div className="flex items-center gap-2.5">
            <h2 className="text-xl font-semibold text-base-content">History</h2>
            {!isReadOnly && headerCheckboxState !== 'none' &&
                <ActionButton type="delete" onClick={onRemove} />
            }
        </div>
    );

    const footer = totalHistoryCount > 0 ? (
        <div className="flex flex-col items-center gap-2 text-xs text-base-content/60">
            {!isLive && <span>Live updates pause on older pages. Go to page 1 for live.</span>}
            <Pagination
                pageNumber={pageNumber}
                totalPages={totalPages}
                totalCount={totalHistoryCount}
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
            {...(totalHistoryCount > 0 ? { badgeText: String(totalHistoryCount) } : {})}
        >
            <PageTable
                headerCheckboxState={headerCheckboxState}
                onHeaderCheckboxChange={onSelectAll}
                footer={footer}
                showCompleted
                selectable={!isReadOnly}
            >
                {historySlots.map(slot =>
                    <HistoryRow
                        key={slot.nzo_id}
                        slot={slot}
                        onIsSelectedChanged={(id, isSelected) => onIsSelectedChanged(new Set<string>([id]), isSelected)}
                        onIsRemovingChanged={(id, isRemoving) => onIsRemovingChanged(new Set<string>([id]), isRemoving)}
                        onRemoved={(id) => onRemoved(new Set([id]))}
                    />
                )}
            </PageTable>

            <ConfirmModal
                show={isConfirmingRemoval}
                title="Remove From History?"
                message={`${selectedCount} item(s) will be removed`}
                checkboxMessage="Delete mounted files"
                onConfirm={onConfirmRemoval}
                onCancel={onCancelRemoval} />
        </PageSection>
    );
}


type HistoryRowProps = {
    slot: PresentationHistorySlot,
    onIsSelectedChanged: (nzo_id: string, isSelected: boolean) => void,
    onIsRemovingChanged: (nzo_id: string, isRemoving: boolean) => void,
    onRemoved: (nzo_id: string) => void
}

export function HistoryRow({ slot, onIsSelectedChanged, onIsRemovingChanged, onRemoved }: HistoryRowProps) {
    const isReadOnly = useIsReadOnly();
    // state
    const [isConfirmingRemoval, setIsConfirmingRemoval] = useState(false);
    const [isRetrying, setIsRetrying] = useState(false);
    const [retryError, setRetryError] = useState<string | null>(null);

    // events
    const onRemove = useCallback(() => {
        setIsConfirmingRemoval(true);
    }, [setIsConfirmingRemoval]);

    const onCancelRemoval = useCallback(() => {
        setIsConfirmingRemoval(false);
    }, [setIsConfirmingRemoval]);

    const onConfirmRemoval = useCallback(async (deleteCompletedFiles?: boolean) => {
        setIsConfirmingRemoval(false);
        onIsRemovingChanged(slot.nzo_id, true);
        try {
            const url = '/api?mode=history&name=delete'
                + `&value=${encodeURIComponent(slot.nzo_id)}`
                + `&del_completed_files=${deleteCompletedFiles ? 1 : 0}`;
            const response = await fetch(url);
            if (response.ok) {
                const data = await response.json();
                if (data.status === true) {
                    onRemoved(slot.nzo_id);
                    return;
                }
            }
        } catch { }
        onIsRemovingChanged(slot.nzo_id, false);
    }, [slot.nzo_id, setIsConfirmingRemoval, onIsRemovingChanged, onRemoved]);

    const onRetry = useCallback(async () => {
        if (!shouldAcceptRetryClick(isRetrying, slot.isRemoving)) return;
        setRetryError(null);
        setIsRetrying(true);
        try {
            const result = await retryHistoryItem(slot.nzo_id);
            if (!result.ok) {
                setRetryError(result.error);
            }
        } finally {
            setIsRetrying(false);
        }
    }, [isRetrying, slot.isRemoving, slot.nzo_id]);

    const folderLink = getExploreContentLink(slot.storage, slot.category);
    const nameHref = folderLink && !slot.isRemoving && !slot.fail_message ? folderLink : null;

    // view
    return (
        <>
            <PageRow
                isSelected={!!slot.isSelected}
                isRemoving={!!slot.isRemoving}
                name={slot.name}
                nameHref={nameHref}
                category={slot.category}
                status={slot.status}
                error={slot.fail_message}
                fileSizeBytes={slot.bytes}
                completed={slot.completed}
                showCompleted
                actions={
                    <div className="flex flex-col items-end gap-1">
                        <div className="flex flex-col items-end justify-center gap-2.5 min-[410px]:flex-row min-[410px]:items-center">
                            <Actions
                                slot={slot}
                                isRetrying={isRetrying}
                                onRemove={onRemove}
                                onRetry={onRetry}
                            />
                        </div>
                        {retryError &&
                            <span role="alert" className="max-w-[180px] text-left text-xs text-error">
                                {retryError}
                            </span>
                        }
                    </div>
                }
                onRowSelectionChanged={isSelected => onIsSelectedChanged(slot.nzo_id, isSelected)}
                selectable={!isReadOnly}
                indexer={slot.indexer}
                providers={slot.providers}
            />
            <ConfirmModal
                show={isConfirmingRemoval}
                title="Remove From History?"
                message={slot.nzb_name}
                errorMessage={slot.fail_message}
                onConfirm={onConfirmRemoval}
                onCancel={onCancelRemoval}
                {...(!slot.fail_message ? { checkboxMessage: "Delete mounted files" } : {})} />
        </>
    );
}

export function Actions({
    slot,
    isRetrying = false,
    onRemove,
    onRetry,
}: {
    slot: PresentationHistorySlot,
    isRetrying?: boolean,
    onRemove: () => void,
    onRetry?: () => void,
}) {
    const isReadOnly = useIsReadOnly();
    const [isMenuOpen, setIsMenuOpen] = useState(false);

    const folderLink = getExploreContentLink(slot.storage, slot.category);

    // determine nzb download URL
    const nzbDownloadUrl = slot.nzb_blob_id
        ? `/api/download-nzb?nzbBlobId=${slot.nzb_blob_id}`
        : null;

    // determine whether explore action should be disabled
    const isFolderDisabled = !folderLink || !!slot.isRemoving || !!slot.fail_message;
    const showRetry = canRetryHistorySlot(slot);

    const onMenuClick = useCallback((e: React.MouseEvent) => {
        e.stopPropagation();
        setIsMenuOpen(x => !x);
    }, []);

    const onRemoveSelected = useCallback(() => {
        setIsMenuOpen(false);
        onRemove?.();
    }, [onRemove]);

    const onRetryClick = useCallback((e: React.MouseEvent) => {
        e.stopPropagation();
        onRetry?.();
    }, [onRetry]);

    return (
        <>
            {!isReadOnly && showRetry &&
                <ActionButton
                    type="retry"
                    disabled={!!slot.isRemoving || isRetrying}
                    onClick={onRetryClick}
                />
            }
            {!isFolderDisabled && folderLink &&
                <Link to={folderLink} discover="none">
                    <ActionButton type="explore" />
                </Link>
            }
            {(isFolderDisabled || !folderLink) &&
                <ActionButton type="explore" disabled />
            }
            {(!isReadOnly || !!nzbDownloadUrl) && <div className="relative">
                <ActionButton
                    type="menu"
                    disabled={!!slot.isRemoving || isRetrying}
                    selected={isMenuOpen}
                    onClick={onMenuClick} />
                <DropdownOptions
                    style={{ marginTop: "5px" }}
                    isOpen={isMenuOpen}
                    onClose={() => setIsMenuOpen(false)}
                    options={[
                        !!nzbDownloadUrl ? { option: <ExportNzb />, linkTo: nzbDownloadUrl } : undefined,
                        !isReadOnly
                            ? { option: <Remove />, onSelect: onRemoveSelected, variant: "danger" }
                            : undefined,
                    ]} />
            </div>}
        </>
    );
}
