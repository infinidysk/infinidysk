// @vitest-environment jsdom
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ManagedEnvProvider } from "~/components/ui";
import { NativeCacheSettings } from "./native-cache";

function Harness({ managed = false, native = false }: { managed?: boolean; native?: boolean }) {
  const [config, setConfig] = useState<Record<string, string>>({ "cache.mode": native ? "native" : "segment", "usenet.segment-cache.enabled": native ? "false" : "true", "cache.native.folders": native ? JSON.stringify([{ id: "disk", name: "NAS", path: "/cache", maxBytes: 1e12, minFreeBytes: 0, maxAgeDays: 0, priority: 0, enabled: true, readOnly: false, storageType: "nas" }]) : "[]" });
  return <ManagedEnvProvider value={managed ? { "cache.mode": "NZBDAV_CONFIG__CACHE__MODE" } : {}}>
    <NativeCacheSettings config={config} setNewConfig={setConfig} />
    <output data-testid="config">{JSON.stringify(config)}</output>
  </ManagedEnvProvider>;
}

describe("native cache folder editor", () => {
  beforeEach(() => vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(JSON.stringify({ activeMode: "segment", configuredMode: "segment", restartRequired: false, reservedBufferBytes: 0, folders: [], jobs: [] }), { status: 200 }))));
  afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

  it("changes the exclusive mode and edits multiple folders without enabling Segment", async () => {
    render(<Harness />);
    await userEvent.selectOptions(screen.getByLabelText("Cache mode (restart required)"), "native");
    await userEvent.click(screen.getByRole("button", { name: "Add cache folder" }));
    await userEvent.click(screen.getByRole("button", { name: "Add cache folder" }));
    expect(screen.getAllByLabelText("Absolute folder path")).toHaveLength(2);
    await userEvent.type(screen.getAllByLabelText("Absolute folder path")[0]!, "/mnt/cache-a");
    const saved = JSON.parse(screen.getByTestId("config").textContent) as Record<string, string>;
    expect(saved["usenet.segment-cache.enabled"]).toBe("false");
    const folders = JSON.parse(saved["cache.native.folders"]!) as { path: string }[];
    expect(folders[0]?.path).toBe("/mnt/cache-a");
    await waitFor(() => expect(screen.getByText(/Running: segment/)).toBeTruthy());
  });

  it("pins an environment-owned cache mode", () => {
    render(<Harness managed />);
    expect(screen.getByLabelText("Cache mode (restart required)").closest("fieldset")?.disabled).toBe(true);
  });

  it("browses bounded cache pages and pins media without changing folder configuration", async () => {
    const key = "a".repeat(64);
    const fetcher = vi.fn().mockImplementation((url: string) => Promise.resolve(new Response(JSON.stringify(url.includes("/entries")
      ? { entries: [{ key, itemId: "movie", name: "Episode 1", length: 100, verifiedBytes: 100, allocatedBytes: 128, pinned: false }], nextAfter: null }
      : { activeMode: "native", configuredMode: "native", restartRequired: false, reservedBufferBytes: 0, folders: [{ id: "disk", online: true, writable: true, committedBytes: 128, entries: 1 }], jobs: [] }), { status: 200 })));
    vi.stubGlobal("fetch", fetcher);
    render(<Harness native />);
    await waitFor(() => expect(screen.getByText(/Online, writable/)).toBeTruthy());
    await userEvent.click(screen.getByRole("button", { name: "View cached files" }));
    await screen.findByText("Episode 1");
    await userEvent.click(screen.getByRole("button", { name: "Pin Episode 1" }));
    expect(fetcher).toHaveBeenCalledWith(expect.stringContaining("/api/native-cache/operations"), expect.objectContaining({
      method: "POST", body: JSON.stringify({ operation: "pin", cacheKey: key, pinned: true }),
    }));
  });
});
