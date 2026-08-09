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

// SABnzbd API (`/api?mode=retry`) response shape
type HistoryRetryResponse = {
    status?: boolean;
    error?: string;
    nzo_id?: string;
};

export async function retryHistoryItem(
    nzoId: string,
    fetchImpl: typeof fetch = fetch,
): Promise<HistoryRetryResult> {
    try {
        const response = await fetchImpl(buildHistoryRetryUrl(nzoId), { method: "POST" });
        let data: HistoryRetryResponse | null = null;
        try {
            data = await response.json() as HistoryRetryResponse;
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
