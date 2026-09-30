// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router";
import NativeCachePage from "./route";

const file = {
  key: "a".repeat(64),
  folderId: "disk",
  itemId: "item",
  name: "Film.mkv",
  length: 100,
  allocatedBytes: 55,
  verifiedBytes: 50,
  pinned: false,
  lastAccessedAt: "2026-09-23T00:00:00Z",
  accessCount: null,
};
const summary = {
  available: true,
  activeMode: "native",
  configuredMode: "native",
  restartRequired: false,
  initializationPending: false,
  initializationError: null,
  asOfUtc: "2026-09-23T00:00:00Z",
  totalAllocatedBytes: 55,
  totalQuotaBytes: 100,
  liveFiles: 1,
  emptyEntries: 0,
  verifiedBlocks: 1,
  retiredEntries: 0,
  retiredBytes: 0,
  traffic: { hitBlocks: 2, hitBytes: 100, missBlocks: 1, missBytes: 50, committedBytes: 50 },
  folders: [
    {
      id: "disk",
      name: "NAS",
      path: "/cache",
      storageType: "nas",
      priority: 1,
      enabled: true,
      readOnly: false,
      maxBytes: 100,
      minFreeBytes: 0,
      online: true,
      writable: true,
      error: null,
      allocatedBytes: 55,
      liveFiles: 1,
      emptyEntries: 0,
      verifiedBlocks: 1,
      retiredEntries: 0,
      retiredBytes: 0,
    },
  ],
};
function json(value: unknown) {
  return new Response(JSON.stringify(value), { status: 200 });
}
function setupFetch() {
  const fetchMock = vi.fn((input: string, _init?: RequestInit) => {
    if (input.includes("/summary")) return Promise.resolve(json(summary));
    if (input.includes("/activity")) return Promise.resolve(json({ items: [file] }));
    if (input.includes("/transfers")) return Promise.resolve(json({ writes: [] }));
    if (input.includes("/files"))
      return Promise.resolve(json({ items: [file], totalCount: 1, nextCursor: null }));
    if (input.includes("/ranges"))
      return Promise.resolve(json({ ranges: [{ offset: 0, count: 50 }], nextAfter: null }));
    if (input.includes("/evictions"))
      return Promise.resolve(json({ items: [], totalCount: 0, nextCursor: null }));
    return Promise.reject(new Error(`Unexpected fetch: ${input}`));
  });
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("Native Cache browser", () => {
  it("shows catalogue metrics and sparse file details without issuing a write request", async () => {
    const fetchMock = setupFetch();
    render(
      <MemoryRouter>
        <NativeCachePage />
      </MemoryRouter>,
    );
    expect(await screen.findByText("66.7%")).toBeTruthy();
    expect(screen.getAllByText("NAS").length).toBeGreaterThan(0);
    await userEvent.click(screen.getByRole("tab", { name: /All files/ }));
    expect(await screen.findByText("Film.mkv")).toBeTruthy();
    await userEvent.click(screen.getByText("Film.mkv"));
    expect(await screen.findByText(/50.0% · 50 B/)).toBeTruthy();
    expect(screen.getByText(/Reads crossing a gap use Usenet/)).toBeTruthy();
    await waitFor(() =>
      expect(fetchMock.mock.calls.some((call) => call[0].includes("/ranges"))).toBe(true),
    );
    expect(fetchMock.mock.calls.every((call) => call[1]?.method === undefined)).toBe(true);
  });

  it("filters eviction history by reason", async () => {
    const fetchMock = setupFetch();
    render(
      <MemoryRouter>
        <NativeCachePage />
      </MemoryRouter>,
    );
    await screen.findByText("66.7%");
    await userEvent.click(screen.getByRole("tab", { name: "Recently evicted" }));
    expect(await screen.findByText("No eviction records found.")).toBeTruthy();
    await userEvent.selectOptions(screen.getByLabelText("Filter eviction reason"), "pressure");
    await waitFor(() =>
      expect(fetchMock.mock.calls.some((call) => call[0].includes("reason=pressure"))).toBe(true),
    );
  });
});
