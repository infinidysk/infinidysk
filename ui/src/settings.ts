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
    mutationFn: (settings: Settings) => body(api.PUT("/api/v1/settings", { body: settings })),
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

const RESTART_WAIT_MS = 120_000;

/** Restarts the server and resolves once the new process answers. */
export function useRestart() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: async () => {
      const before = (await body(api.GET("/api/v1/status"))).started_ms;
      await body(api.POST("/api/v1/restart"));
      for (const deadline = Date.now() + RESTART_WAIT_MS; Date.now() < deadline; ) {
        await new Promise((resolve) => setTimeout(resolve, 1000));
        const status = await body(api.GET("/api/v1/status")).catch(() => null);
        if (status && status.started_ms !== before) return;
      }
      throw new Error("infinidysk has not come back after 2 minutes; check its logs");
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
