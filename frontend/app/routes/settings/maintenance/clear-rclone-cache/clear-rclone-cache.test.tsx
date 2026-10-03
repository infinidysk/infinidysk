// @vitest-environment jsdom
/* global HTMLDialogElement */
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ClearRcloneCache } from "./clear-rclone-cache";

const fetchMock = vi.fn<typeof fetch>();

beforeEach(() => {
  vi.stubGlobal("fetch", fetchMock);
  if (!HTMLDialogElement.prototype.showModal) {
    HTMLDialogElement.prototype.showModal = function showModal() {
      this.open = true;
    };
    HTMLDialogElement.prototype.close = function close() {
      this.open = false;
    };
  }
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

async function confirmClear() {
  // The panel button and the modal's confirm button read the same, so the
  // confirm has to be looked up inside the dialog.
  await userEvent.click(screen.getByRole("button", { name: /clear cache/i }));
  const dialog = await screen.findByRole("dialog");
  await userEvent.click(within(dialog).getByRole("button", { name: /^clear cache$/i }));
}

describe("ClearRcloneCache", () => {
  it("reports how much space the purge freed", async () => {
    fetchMock.mockResolvedValue(
      jsonResponse({ status: true, freedBytes: 3 * 1024 ** 3, mounted: ["/data/nzbdav"] }),
    );

    render(createElement(ClearRcloneCache));
    await confirmClear();

    await waitFor(() => expect(screen.getByText(/Freed 3\.0 GB/)).toBeTruthy());
    expect(screen.getByText(/Remounted 1 share/)).toBeTruthy();
  });

  it("surfaces a purge the backend refused instead of claiming success", async () => {
    // A cache directory that could not be emptied still returns 200 with the
    // reason in errors; reporting that as freed space would be a lie.
    fetchMock.mockResolvedValue(
      jsonResponse({
        status: true,
        freedBytes: 0,
        errors: ["'/config/rclone/cache' is a filesystem root, so it was not emptied."],
      }),
    );

    render(createElement(ClearRcloneCache));
    await confirmClear();

    await waitFor(() => expect(screen.getByText(/was not emptied/)).toBeTruthy());
  });

  it("reports a failed request instead of failing silently", async () => {
    fetchMock.mockResolvedValue(jsonResponse({ error: "Daemon is not running" }, 500));

    render(createElement(ClearRcloneCache));
    await confirmClear();

    await waitFor(() => expect(screen.getByText(/Daemon is not running/)).toBeTruthy());
  });
});
