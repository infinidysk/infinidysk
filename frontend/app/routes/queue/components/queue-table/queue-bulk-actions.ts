export type QueueBulkSlot = {
  status: string;
  isUploading?: boolean;
  isRemoving?: boolean;
};

export function canPauseQueueSlot(slot: QueueBulkSlot): boolean {
  return !slot.isUploading && slot.status !== "Paused" && !slot.isRemoving;
}

export function canResumeQueueSlot(slot: QueueBulkSlot): boolean {
  return !slot.isUploading && slot.status === "Paused" && !slot.isRemoving;
}

export function buildClearQueueUrl(category?: string): string {
  const base = "/api?mode=queue&name=delete&value=all";
  if (!category) return base;
  return `${base}&cat=${encodeURIComponent(category)}`;
}

export function buildQueuePauseResumeUrl(action: "pause" | "resume"): string {
  return `/api?mode=queue&name=${action}`;
}

export function buildSetQueuePriorityUrl(priority: string): string {
  return `/api?mode=queue&name=priority&value2=${encodeURIComponent(priority)}`;
}

export function buildSetQueueCategoryUrl(category: string): string {
  return `/api?mode=change_cat&cat=${encodeURIComponent(category)}`;
}

async function postQueueIds(url: string, nzoIds: string[]): Promise<boolean> {
  if (nzoIds.length === 0) return false;
  try {
    const response = await fetch(url, {
      method: "POST",
      headers: { "Content-Type": "application/json;charset=UTF-8" },
      body: JSON.stringify({ nzo_ids: nzoIds }),
    });
    if (!response.ok) return false;
    const data = (await response.json()) as { status?: boolean };
    return data.status === true;
  } catch {
    return false;
  }
}

export function postQueuePause(nzoIds: string[]): Promise<boolean> {
  return postQueueIds(buildQueuePauseResumeUrl("pause"), nzoIds);
}

export function postQueueResume(nzoIds: string[]): Promise<boolean> {
  return postQueueIds(buildQueuePauseResumeUrl("resume"), nzoIds);
}

export function postQueuePriority(nzoIds: string[], priority: string): Promise<boolean> {
  return postQueueIds(buildSetQueuePriorityUrl(priority), nzoIds);
}

export function postQueueCategory(nzoIds: string[], category: string): Promise<boolean> {
  return postQueueIds(buildSetQueueCategoryUrl(category), nzoIds);
}

export async function postClearQueue(category?: string): Promise<boolean> {
  try {
    const response = await fetch(buildClearQueueUrl(category), { method: "POST" });
    if (!response.ok) return false;
    const data = (await response.json()) as { status?: boolean };
    return data.status === true;
  } catch {
    return false;
  }
}
