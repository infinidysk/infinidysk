import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, body, type Schemas } from "./api/client";

export type Settings = Schemas["Settings"];

export function useSettings() {
  return useQuery({ queryKey: ["settings"], queryFn: () => body(api.GET("/api/v1/settings")) });
}

export function useCatalog() {
  return useQuery({
    queryKey: ["catalog"],
    queryFn: () => body(api.GET("/api/v1/catalog")),
    staleTime: Infinity,
  });
}

/** Saves the whole settings document; masked secrets sent back unchanged are kept. */
export function useSaveSettings() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ settings, indexers_revision }: { settings: Settings; indexers_revision: string }) =>
      body(api.PUT("/api/v1/settings", { body: { ...settings, indexers_revision } })),
    onSuccess: (view) => {
      client.setQueryData(["settings"], view);
      void client.invalidateQueries({ queryKey: ["auth"] });
    },
  });
}

/** The server sends this in place of stored secrets. */
export const MASK = "********";

/** Keeps password managers from filling or saving API keys. */
export const notAPassword = {
  autoComplete: "off",
  "data-1p-ignore": true,
  "data-lpignore": "true",
  "data-form-type": "other",
  "data-bwignore": true,
} as const;

const RESTART_WAIT_MS = 5 * 60_000;
// One poll request; a process that accepts the connection but never answers must not stall the wait.
const POLL_REQUEST_MS = 10_000;

export type Restarted = "restarted" | "signed-out";

/**
 * Polls until a new process answers. `before` is `started_ms` from before the request;
 * `attempt` is the id the restore or undo request returned. Uses only server values: the
 * browser clock is never compared with a server time.
 */
export async function waitForRestart(
  before: number,
  opts: {
    attempt?: string;
    pollMs?: number;
    deadlineMs?: number;
    onProgress?: (running: Schemas["LiveMaintenance"] | null) => void;
  } = {},
): Promise<Restarted> {
  const { attempt, pollMs = 1000, deadlineMs = RESTART_WAIT_MS, onProgress } = opts;
  // Set once the old process may be gone; a reported `staging` attempt clears it.
  let deadline: number | null = null;
  const start = () => {
    deadline ??= Date.now() + deadlineMs;
  };
  // `null` when the request did not reach a process.
  const get = async <T>(request: Promise<{ data?: T; response: Response }>) => {
    try {
      const { data, response } = await request;
      return { data, status: response.status };
    } catch {
      return null;
    }
  };
  for (;;) {
    if (deadline != null && Date.now() >= deadline) throw new Error("infinidysk has not come back after 5 minutes; check its logs");
    await new Promise((resolve) => setTimeout(resolve, pollMs));
    const status = await get(api.GET("/api/v1/status", { signal: AbortSignal.timeout(POLL_REQUEST_MS) }));
    if (status?.status === 401) return "signed-out";
    if (!status || status.status >= 500 || !status.data) {
      start();
      continue;
    }
    if (status.data.started_ms !== before) return "restarted";
    const maintenance = await get(api.GET("/api/v1/maintenance", { signal: AbortSignal.timeout(POLL_REQUEST_MS) }));
    if (maintenance?.status === 401) return "signed-out";
    if (maintenance?.data) onProgress?.(maintenance.data.running ?? null);
    let staging = false;
    if (attempt) {
      const backups = await get(api.GET("/api/v1/backups", { signal: AbortSignal.timeout(POLL_REQUEST_MS) }));
      if (backups?.status === 401) return "signed-out";
      const mine = backups?.data?.attempt?.id === attempt ? backups.data.attempt : null;
      if (mine?.state === "failed") throw new Error(mine.reason ?? "The restore failed.");
      staging = mine?.state === "staging";
    }
    if (staging) deadline = null;
    else start();
  }
}

/** Restarts the server and resolves once the new process answers. */
export function useRestart() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: async () => {
      const before = (await body(api.GET("/api/v1/status"))).started_ms;
      await body(api.POST("/api/v1/restart"));
      if ((await waitForRestart(before)) === "signed-out") window.location.reload();
    },
    onSuccess: () => void client.invalidateQueries(),
  });
}

/** A v4 UUID; `crypto.randomUUID` is missing on plain-HTTP LAN addresses. */
export function uuid(): string {
  const b = crypto.getRandomValues(new Uint8Array(16));
  b[6] = (b[6]! & 0x0f) | 0x40;
  b[8] = (b[8]! & 0x3f) | 0x80;
  const hex = Array.from(b, (x) => x.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

/** Copies `text`; `navigator.clipboard` is missing on plain-HTTP LAN addresses. */
export async function copyText(text: string): Promise<void> {
  if (navigator.clipboard) return navigator.clipboard.writeText(text);
  const active = document.activeElement;
  const node = document.createElement("textarea");
  node.value = text;
  node.readOnly = true;
  node.style.cssText = "position:fixed;top:0;left:0;opacity:0;font-size:16px";
  // Keep the temporary control inside the modal's focus trap.
  (active?.closest('[role="dialog"]') ?? document.body).append(node);
  let copied = false;
  try {
    node.focus({ preventScroll: true });
    node.select();
    copied = document.execCommand("copy");
  } catch {
    copied = false;
  } finally {
    node.remove();
    if (active instanceof HTMLElement) active.focus({ preventScroll: true });
  }
  if (copied) return;
  window.prompt("Copy text", text);
  throw new Error("Automatic copying was blocked by the browser");
}
