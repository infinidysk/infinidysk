/** Slice a combined queue-then-history list into API windows for one page. */
export function combinedListWindow(
  queueCount: number,
  page: number,
  pageSize: number,
): {
  queueStart: number;
  queueLimit: number;
  historyStart: number;
  historyLimit: number;
} {
  const start = Math.max(0, (page - 1) * pageSize);
  const end = start + pageSize;
  const queueLimit = start >= queueCount ? 0 : Math.min(pageSize, Math.max(0, queueCount - start));
  const historyStart = Math.max(0, start - queueCount);
  const historyLimit = Math.max(0, end - Math.max(start, queueCount));
  return {
    queueStart: start,
    queueLimit,
    historyStart,
    historyLimit,
  };
}

const QUEUE_STATUSES = new Set(["Downloading", "Queued", "Paused"]);
const HISTORY_STATUSES = new Set(["Completed", "Failed"]);

export function statusAppliesToQueue(status: string): boolean {
  return status === "" || QUEUE_STATUSES.has(status);
}

export function statusAppliesToHistory(status: string): boolean {
  return status === "" || HISTORY_STATUSES.has(status);
}
