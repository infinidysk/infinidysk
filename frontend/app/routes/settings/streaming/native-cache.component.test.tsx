// @vitest-environment jsdom
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ManagedEnvProvider } from "~/components/ui";
import { NativeCacheSettings } from "./native-cache";

function Harness({ managed = false, native = false }: { managed?: boolean; native?: boolean }) {
  const [config, setConfig] = useState<Record<string, string>>({
    "cache.mode": native ? "native" : "segment",
    "usenet.segment-cache.enabled": native ? "false" : "true",
    "cache.native.folders": native
      ? JSON.stringify([
          {
            id: "disk",
            name: "NAS",
            path: "/cache",
            maxBytes: 1e12,
            minFreeBytes: 0,
            maxAgeDays: 0,
            priority: 0,
            enabled: true,
            readOnly: false,
            storageType: "nas",
          },
        ])
      : "[]",
  });
  return (
    <ManagedEnvProvider value={managed ? { "cache.mode": "NZBDAV_CONFIG__CACHE__MODE" } : {}}>
      <NativeCacheSettings config={config} setNewConfig={setConfig} />
      <output data-testid="config">{JSON.stringify(config)}</output>
    </ManagedEnvProvider>
  );
}

describe("native cache folder editor", () => {
  beforeEach(() =>
    vi.stubGlobal(
      "fetch",
      vi
        .fn()
        .mockResolvedValue(
          new Response(
            JSON.stringify({
              activeMode: "segment",
              configuredMode: "segment",
              restartRequired: false,
              reservedBufferBytes: 0,
              folders: [],
              jobs: [],
            }),
            { status: 200 },
          ),
        ),
    ),
  );
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

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
    expect(
      screen.getByLabelText("Cache mode (restart required)").closest("fieldset")?.disabled,
    ).toBe(true);
  });

  it("shows measured probe capabilities separately from the storage hint and aggregate counters", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(
        new Response(
          JSON.stringify({
            activeMode: "native",
            configuredMode: "native",
            reservedBufferBytes: 0,
            folders: [],
            counters: {
              hitBlocks: 12,
              missBlocks: 2,
              committedBytes: 1e9,
              fallbacks: 3,
              ioTimeouts: 1,
            },
            jobs: [
              {
                id: "probe",
                folderId: "disk",
                operation: "probe",
                state: "completed",
                probe: {
                  fileSystem: "nfs",
                  capability: "nfs",
                  readable: true,
                  writable: true,
                  durableWriteVerified: true,
                  availableBytes: 2e12,
                },
              },
            ],
          }),
          { status: 200 },
        ),
      ),
    );
    render(<Harness native />);
    await screen.findByText(/Verified block hits: 12/);
    expect(screen.getByText(/nfs \/ nfs/).textContent).toContain("durable write verified: yes");
  });

  it("shows folder capacity and keeps tuning and maintenance in disclosures", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(
        new Response(
          JSON.stringify({
            activeMode: "native",
            configuredMode: "native",
            restartRequired: false,
            reservedBufferBytes: 32 * 1048576,
            folders: [
              { id: "disk", online: true, writable: true, committedBytes: 250e9, entries: 4 },
            ],
            jobs: [],
          }),
        ),
      ),
    );
    render(<Harness native />);
    await screen.findByText(/Online, writable/);
    expect(screen.getAllByText("25.0%")).toHaveLength(2);
    const editor = screen.getByText("Edit folder settings").closest("details");
    expect(editor?.open).toBe(false);
    await userEvent.click(screen.getByText("Edit folder settings"));
    expect(editor?.open).toBe(true);
    expect(screen.getByLabelText("Free-space reserve (GB)")).toBeTruthy();
    const maintenance = screen.getByText("Maintenance").closest("details");
    expect(maintenance?.open).toBe(false);
    await userEvent.click(screen.getByText("Maintenance"));
    expect(screen.getByRole("button", { name: "Clear eligible files" })).toBeTruthy();
  });

  it("browses bounded cache pages and pins media without changing folder configuration", async () => {
    const key = "a".repeat(64);
    const fetcher = vi
      .fn()
      .mockImplementation((url: string) =>
        Promise.resolve(
          new Response(
            JSON.stringify(
              url.includes("/entries")
                ? {
                    entries: [
                      {
                        key,
                        itemId: "movie",
                        name: "Episode 1",
                        generation: "source-revision",
                        length: 100,
                        verifiedBytes: 100,
                        allocatedBytes: 128,
                        pinned: false,
                      },
                    ],
                    nextAfter: null,
                  }
                : url.includes("/ranges")
                  ? { ranges: [{ offset: 0, count: 100 }], nextAfter: null }
                  : {
                      activeMode: "native",
                      configuredMode: "native",
                      restartRequired: false,
                      reservedBufferBytes: 0,
                      folders: [
                        {
                          id: "disk",
                          online: true,
                          writable: true,
                          committedBytes: 128,
                          entries: 1,
                        },
                      ],
                      jobs: [],
                    },
            ),
            { status: 200 },
          ),
        ),
      );
    vi.stubGlobal("fetch", fetcher);
    render(<Harness native />);
    await waitFor(() => expect(screen.getByText(/Online, writable/)).toBeTruthy());
    await userEvent.click(screen.getByRole("button", { name: "View cached files" }));
    await screen.findByText("Episode 1");
    expect(screen.getByText(/source-revision/)).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Verified ranges for Episode 1" }));
    expect(await screen.findByText("Bytes 0–99")).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Pin Episode 1" }));
    expect(fetcher).toHaveBeenCalledWith(
      expect.stringContaining("/api/native-cache/operations"),
      expect.objectContaining({
        method: "POST",
        body: JSON.stringify({ operation: "pin", cacheKey: key, pinned: true }),
      }),
    );
  });

  it("confirms file eviction and disables it for pinned or read-only entries", async () => {
    const key = "b".repeat(64);
    const confirm = vi.fn().mockReturnValue(false);
    vi.stubGlobal("confirm", confirm);
    const fetcher = vi.fn().mockImplementation((url: string) =>
      Promise.resolve(
        new Response(
          JSON.stringify(
            url.includes("/entries")
              ? {
                  entries: [
                    {
                      key,
                      itemId: "episode",
                      name: "Episode 2",
                      generation: "current-revision",
                      length: 100,
                      verifiedBytes: 100,
                      allocatedBytes: 128,
                      pinned: false,
                    },
                  ],
                  nextAfter: null,
                }
              : {
                  activeMode: "native",
                  configuredMode: "native",
                  restartRequired: false,
                  reservedBufferBytes: 0,
                  folders: [
                    {
                      id: "disk",
                      online: true,
                      writable: true,
                      committedBytes: 128,
                      entries: 1,
                    },
                  ],
                  jobs: [],
                },
          ),
          { status: 200 },
        ),
      ),
    );
    vi.stubGlobal("fetch", fetcher);
    render(<Harness native />);
    await screen.findByText(/Online, writable/);
    await userEvent.click(screen.getByRole("button", { name: "View cached files" }));
    const evict = await screen.findByRole("button", {
      name: "Evict Episode 2 from Native Cache",
    });

    await userEvent.click(evict);
    expect(confirm).toHaveBeenCalledOnce();
    expect(fetcher.mock.calls.some(([, init]) => init?.body?.includes('"operation":"evict"'))).toBe(
      false,
    );

    confirm.mockReturnValue(true);
    await userEvent.click(evict);
    expect(fetcher).toHaveBeenCalledWith(
      expect.stringContaining("/api/native-cache/operations"),
      expect.objectContaining({
        method: "POST",
        body: JSON.stringify({
          operation: "evict",
          folderId: "disk",
          cacheKey: key,
          confirmCacheKey: key,
        }),
      }),
    );

    await waitFor(() => expect(evict).toHaveProperty("disabled", false));
    await userEvent.click(screen.getByRole("button", { name: "Pin Episode 2" }));
    expect(evict).toHaveProperty("disabled", true);
    await userEvent.click(screen.getByRole("button", { name: "Unpin Episode 2" }));
    expect(evict).toHaveProperty("disabled", false);
    await userEvent.click(screen.getByText("Edit folder settings"));
    await userEvent.click(
      screen.getByLabelText("Read-only (hits/import only; no writes or eviction)"),
    );
    expect(evict).toHaveProperty("disabled", true);
  });
});
