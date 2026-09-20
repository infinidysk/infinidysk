// @vitest-environment jsdom

import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { WardenSettings } from "./warden";

const EMPTY_SNAPSHOT = {
  quorum: 2,
  localCount: 0,
  effectiveCount: 0,
  totalRows: 0,
  sources: [],
};

const SCAN = {
  dryRun: true,
  scanned: 1200,
  eligible: 900,
  distinct: 850,
  added: 0,
  skippedMissingNzb: 40,
  skippedUnparsableNzb: 0,
  skippedNoFingerprint: 10,
};

function jsonResponse(body: unknown) {
  return { ok: true, json: () => Promise.resolve(body) } as unknown as Response;
}

// The component only ever passes string urls, so the mock takes one and the assertions
// below can read it back without coercing a Request or URL.
function stubFetch(handler: (url: string, init?: RequestInit) => Response) {
  const fetchMock = vi.fn((url: string, init?: RequestInit) => Promise.resolve(handler(url, init)));
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

function renderWarden() {
  render(<WardenSettings config={{}} setNewConfig={() => {}} />);
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

beforeEach(() => {
  // jsdom ships no dialog behavior; the modal only needs to open to be asserted on.
  if (!HTMLDialogElement.prototype.showModal) {
    HTMLDialogElement.prototype.showModal = function (this: HTMLDialogElement) {
      this.open = true;
    };
    HTMLDialogElement.prototype.close = function (this: HTMLDialogElement) {
      this.open = false;
    };
  }
});

describe("WardenSettings history scan", () => {
  it("previews the scan before writing anything", async () => {
    const fetchMock = stubFetch((url) => {
      if (url.includes("/api/warden-import-history")) return jsonResponse(SCAN);
      if (url.includes("/api/warden-sources")) return jsonResponse(EMPTY_SNAPSHOT);
      return jsonResponse({});
    });
    renderWarden();

    await userEvent.click(screen.getByRole("button", { name: /scan history/i }));

    await waitFor(() => {
      expect(screen.getByText(/850 distinct fingerprints/)).toBeTruthy();
    });
    // The preview must never write: the only history call so far is the dry run.
    const historyCalls = fetchMock.mock.calls.filter(([url]) =>
      url.includes("/api/warden-import-history"),
    );
    expect(historyCalls).toHaveLength(1);
    expect(historyCalls[0]?.[0]).toContain("dryRun=true");
    expect(screen.getByText(/40 with no stored nzb/)).toBeTruthy();
    expect(screen.getByText(/10 without a poster or post date/)).toBeTruthy();
  });

  it("writes only after the second, explicit click", async () => {
    const fetchMock = stubFetch((url) => {
      if (url.includes("/api/warden-import-history")) {
        return jsonResponse(
          url.includes("dryRun=false") ? { ...SCAN, dryRun: false, added: 850 } : SCAN,
        );
      }
      if (url.includes("/api/warden-sources")) return jsonResponse(EMPTY_SNAPSHOT);
      return jsonResponse({});
    });
    renderWarden();

    await userEvent.click(screen.getByRole("button", { name: /scan history/i }));
    await waitFor(() => screen.getByRole("button", { name: /add to my list/i }));
    await userEvent.click(screen.getByRole("button", { name: /add to my list/i }));

    await waitFor(() => {
      expect(
        screen.getByText(/Added 850 fingerprints from your own failed imports\./),
      ).toBeTruthy();
    });
    expect(fetchMock.mock.calls.filter(([url]) => url.includes("dryRun=false"))).toHaveLength(1);
  });

  it("offers no write button when the scan found nothing", async () => {
    stubFetch((url) => {
      if (url.includes("/api/warden-import-history")) {
        return jsonResponse({ ...SCAN, eligible: 0, distinct: 0 });
      }
      if (url.includes("/api/warden-sources")) return jsonResponse(EMPTY_SNAPSHOT);
      return jsonResponse({});
    });
    renderWarden();

    await userEvent.click(screen.getByRole("button", { name: /scan history/i }));

    await waitFor(() => {
      expect(screen.getByText(/Nothing to add\./)).toBeTruthy();
    });
    expect(screen.queryByRole("button", { name: /add to my list/i })).toBeNull();
  });
});
