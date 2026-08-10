export type RetryableHistorySlot = {
    status: string;
    nzb_blob_id?: string | null;
};

export function canRetryHistorySlot(slot: RetryableHistorySlot): boolean {
    return slot.status === "Failed" && !!slot.nzb_blob_id;
}

export function shouldAcceptRetryClick(isRetrying: boolean, isRemoving?: boolean): boolean {
    return !isRetrying && !isRemoving;
}

export function buildHistoryRetryUrl(nzoId: string): string {
    return `/api?mode=retry&value=${encodeURIComponent(nzoId)}`;
}

export type HistoryRetryResult =
    | { ok: true; nzoId?: string | undefined }
    | { ok: false; error: string };

export async function retryHistoryItem(
    nzoId: string,
    fetchImpl: typeof fetch = fetch,
): Promise<HistoryRetryResult> {
    try {
        const response = await fetchImpl(buildHistoryRetryUrl(nzoId), { method: "POST" });
        let data: { status?: boolean; error?: string; nzo_id?: string } | null = null;
        try {
            data = await response.json();
        } catch {
            data = null;
        }

        if (response.ok && data?.status === true) {
            return { ok: true, nzoId: data.nzo_id };
        }

        return {
            ok: false,
            error: data?.error || "Failed to retry history item.",
        };
    } catch {
        return { ok: false, error: "Failed to retry history item." };
    }
}


export type BulkHistoryRetryResult = {
    ok: boolean;
    succeeded: string[];
    failed: Array<{ nzoId: string; error: string }>;
};

export async function retryHistoryItems(
    nzoIds: string[],
    fetchImpl: typeof fetch = fetch,
): Promise<BulkHistoryRetryResult> {
    if (nzoIds.length === 0) {
        return { ok: false, succeeded: [], failed: [] };
    }

    try {
        const response = await fetchImpl("/api?mode=retry", {
            method: "POST",
            headers: { "Content-Type": "application/json;charset=UTF-8" },
            body: JSON.stringify({ nzo_ids: nzoIds }),
        });
        let data: {
            status?: boolean;
            error?: string;
            nzo_ids?: string[];
            failed?: Array<{ nzo_id: string; error: string }>;
        } | null = null;
        try {
            data = await response.json();
        } catch {
            data = null;
        }

        const succeeded = data?.nzo_ids ?? [];
        const failed = (data?.failed ?? []).map((item) => ({
            nzoId: item.nzo_id,
            error: item.error,
        }));

        if (response.ok && data?.status === true) {
            return { ok: true, succeeded, failed };
        }

        return {
            ok: false,
            succeeded,
            failed: failed.length > 0
                ? failed
                : [{ nzoId: nzoIds[0]!, error: data?.error || "Failed to retry history items." }],
        };
    } catch {
        return {
            ok: false,
            succeeded: [],
            failed: [{ nzoId: nzoIds[0]!, error: "Failed to retry history items." }],
        };
    }
}
