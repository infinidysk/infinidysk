import type {
  HealthCheckQueueItem,
  HealthResult,
  RepairAction,
} from "~/clients/backend-client.server";

export type HealthQueueState = {
  items: HealthCheckQueueItem[];
  uncheckedCount: number;
};

// Numeric values mirror the backend enums in backend-client.server, which cannot be
// value-imported into this client module.
const healthResultValues: readonly HealthResult[] = [0, 1, 2];
const repairActionValues: readonly RepairAction[] = [0, 1, 2, 3, 4];

function includesNumericValue(values: readonly number[], value: number): boolean {
  return values.includes(value);
}

export function completeHealthCheck(state: HealthQueueState, davItemId: string): HealthQueueState {
  const completedItem = state.items.find((item) => item.id === davItemId);
  if (!completedItem) return state;

  return {
    items: state.items.filter((item) => item.id !== davItemId),
    uncheckedCount:
      completedItem.nextHealthCheck === null
        ? Math.max(0, state.uncheckedCount - 1)
        : state.uncheckedCount,
  };
}

export function updateHealthCheckProgress(
  state: HealthQueueState,
  davItemId: string,
  progress: number,
): HealthQueueState {
  if (!state.items.some((item) => item.id === davItemId)) return state;

  return {
    ...state,
    items: state.items.map((item) => (item.id === davItemId ? { ...item, progress } : item)),
  };
}

export function parseHealthItemProgressMessage(message: string): {
  davItemId: string;
  progress: number;
} | null {
  const parts = message.split("|");
  if (parts.length !== 2) return null;
  const [davItemId, progressValue] = parts;
  if (
    !davItemId ||
    progressValue === undefined ||
    progressValue.trim() === "" ||
    progressValue === "done"
  )
    return null;
  const progress = Number(progressValue);
  if (!Number.isFinite(progress) || progress < 0 || progress > 100) return null;
  return { davItemId, progress };
}

export function parseHealthItemStatusMessage(message: string): {
  davItemId: string;
  healthResult: HealthResult;
  repairAction: RepairAction;
} | null {
  const parts = message.split("|");
  if (parts.length !== 3) return null;
  const [davItemId, healthResultValue, repairActionValue] = parts;
  if (
    !davItemId ||
    healthResultValue === undefined ||
    repairActionValue === undefined ||
    healthResultValue.trim() === "" ||
    repairActionValue.trim() === ""
  )
    return null;
  const healthResult = Number(healthResultValue);
  const repairAction = Number(repairActionValue);
  if (
    !Number.isInteger(healthResult) ||
    !includesNumericValue(healthResultValues, healthResult) ||
    !Number.isInteger(repairAction) ||
    !includesNumericValue(repairActionValues, repairAction)
  )
    return null;
  return { davItemId, healthResult, repairAction };
}

export function getVisibleHealthCheckItems(
  items: HealthCheckQueueItem[],
  maximumCount = 10,
): HealthCheckQueueItem[] {
  const progressing = items.filter((item) => (item.progress ?? 0) > 0);
  const waiting = items.filter((item) => (item.progress ?? 0) <= 0);
  return [...progressing, ...waiting].slice(0, maximumCount);
}
