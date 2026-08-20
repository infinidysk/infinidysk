import { useCallback, useEffect, useState } from "react";

const STORAGE_KEY = "overview-row-order";

export function useRowOrder(defaultOrder: readonly string[]) {
  const [order, setOrder] = useState<string[]>(() => [...defaultOrder]);

  useEffect(() => {
    try {
      const raw = globalThis.localStorage?.getItem(STORAGE_KEY);
      if (!raw) return;
      const saved: unknown = JSON.parse(raw);
      if (!Array.isArray(saved)) return;
      const known = new Set(defaultOrder);
      const kept = saved.filter(
        (id: unknown): id is string => typeof id === "string" && known.has(id),
      );
      const missing = defaultOrder.filter((id) => !kept.includes(id));
      setOrder([...kept, ...missing]);
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
