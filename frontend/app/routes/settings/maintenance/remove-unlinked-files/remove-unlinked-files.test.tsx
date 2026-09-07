// @vitest-environment jsdom
/* global HTMLDialogElement */
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { RemoveUnlinkedFiles } from "./remove-unlinked-files";

const fetchMock = vi.fn<typeof fetch>();
const websocketTopicMocks = vi.hoisted(() => ({
  setProgress: null as ((message: string) => void) | null,
  onOpen: null as (() => void) | null,
}));

vi.mock("~/utils/shared-websocket", () => ({
  useWebsocketTopic: (
    _topic: string,
    _kind: string,
    onMessage: (message: string) => void,
    options: { onOpen?: () => void },
  ) => {
    websocketTopicMocks.setProgress = onMessage;
    websocketTopicMocks.onOpen = options.onOpen ?? null;
  },
}));

const savedConfig = { "media.library-dir": "/library" };

beforeEach(() => {
  vi.stubGlobal("fetch", fetchMock);
  websocketTopicMocks.setProgress = null;
  websocketTopicMocks.onOpen = null;
  if (!HTMLDialogElement.prototype.showModal) {
    HTMLDialogElement.prototype.showModal = function (this: HTMLDialogElement) {
      this.open = true;
    };
    HTMLDialogElement.prototype.close = function (this: HTMLDialogElement) {
      this.open = false;
    };
  }
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { "Content-Type": "application/json" },
  });
}

describe("RemoveUnlinkedFiles", () => {
  it("sends the reviewed dry-run approval with an exceptional cleanup", async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ status: true, previewToken: "approval-token" }))
      .mockResolvedValueOnce(jsonResponse({ status: true }));
    const user = userEvent.setup();
    render(createElement(RemoveUnlinkedFiles, { savedConfig }));
    websocketTopicMocks.onOpen?.();

    await user.click(screen.getByRole("button", { name: "Dry Run" }));
    await screen.findByText(/High-volume cleanup is unlocked/);
    websocketTopicMocks.setProgress?.("Dry Run - Done. Identified 1730 unlinked files.");
    await waitFor(() => {
      expect(screen.getByRole("button", { name: "Run Task" })).toHaveProperty("disabled", false);
    });
    await user.click(screen.getByRole("button", { name: "Run Task" }));
    await user.click(
      screen.getByRole("checkbox", {
        name: "I reviewed the dry-run audit and have a current /config backup",
      }),
    );
    await user.click(screen.getByRole("button", { name: "Remove orphaned files" }));

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2));
    expect(fetchMock).toHaveBeenNthCalledWith(2, "/api/remove-unlinked-files", {
      headers: { "X-InfiniDysk-Cleanup-Preview": "approval-token" },
    });
  });
});
