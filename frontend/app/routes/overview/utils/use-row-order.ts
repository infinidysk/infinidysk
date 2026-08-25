import { useCallback, useEffect, useState } from "react";

const STORAGE_KEY = "overview-row-order";

/**
 * Merge a saved layout with the default row order. Saved rows keep their
 * relative order; rows the saved layout doesn't know about (newly shipped
 * widgets) are spliced in at their default position — before the first
 * default-successor present in the saved layout — instead of being dumped
 * at the bottom. Unknown saved ids are dropped.
 */
export function mergeRowOrder(defaultOrder: readonly string[], saved: unknown): string[] {
  const known = new Set(defaultOrder);
  const merged = Array.isArray(saved)
    ? saved.filter((id: unknown): id is string => typeof id === "string" && known.has(id))
    : [];
  for (const id of defaultOrder) {
    if (merged.includes(id)) continue;
    const anchor = defaultOrder
      .slice(defaultOrder.indexOf(id) + 1)
      .find((successor) => merged.includes(successor));
    if (anchor === undefined) merged.push(id);
    else merged.splice(merged.indexOf(anchor), 0, id);
  }
  return merged;
}

export function useRowOrder(defaultOrder: readonly string[]) {
  const [order, setOrder] = useState<string[]>(() => [...defaultOrder]);

  useEffect(() => {
    try {
      const raw = globalThis.localStorage?.getItem(STORAGE_KEY);
      if (!raw) return;
      setOrder(mergeRowOrder(defaultOrder, JSON.parse(raw)));
    } catch {
      /* ignore corrupt storage */
    }
  }, [defaultOrder]);

  const save = useCallback((next: string[]) => {
    setOrder(next);
    try {
      globalThis.localStorage?.setItem(STORAGE_KEY, JSON.stringify(next));
    } catch {
      /* ignore quota / private mode */
    }
  }, []);

  const reset = useCallback(() => {
    setOrder([...defaultOrder]);
    try {
      globalThis.localStorage?.removeItem(STORAGE_KEY);
    } catch {
      /* ignore */
    }
  }, [defaultOrder]);

  return { order, save, reset };
}
