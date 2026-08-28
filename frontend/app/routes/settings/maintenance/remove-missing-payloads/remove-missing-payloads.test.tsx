// @vitest-environment jsdom
/* global HTMLDialogElement */
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { RemoveMissingPayloads } from "./remove-missing-payloads";

const fetchMock = vi.fn<typeof fetch>();
const websocketTopicMocks = vi.hoisted(() => ({
  setProgress: null as ((message: string) => void) | null,
  onOpen: null as (() => void) | null,
  onClose: null as (() => void) | null,
}));

vi.mock("~/utils/shared-websocket", () => ({
  useWebsocketTopic: (
    _topic: string,
    _kind: string,
    onMessage: (message: string) => void,
    options: { onOpen?: () => void; onClose?: () => void },
  ) => {
    websocketTopicMocks.setProgress = onMessage;
    websocketTopicMocks.onOpen = options.onOpen ?? null;
    websocketTopicMocks.onClose = options.onClose ?? null;
  },
}));

const savedConfig = { "media.library-dir": "/library" };

beforeEach(() => {
  vi.stubGlobal("fetch", fetchMock);
  websocketTopicMocks.setProgress = null;
  websocketTopicMocks.onOpen = null;
  websocketTopicMocks.onClose = null;
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

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

describe("RemoveMissingPayloads", () => {
  it("locks controls while the dry-run POST is pending even when terminal websocket state arrives first", async () => {
    let resolveResponse: ((response: Response) => void) | undefined;
    fetchMock.mockReturnValueOnce(
      new Promise<Response>((resolve) => {
        resolveResponse = resolve;
      }),
    );
    const user = userEvent.setup();
    render(createElement(RemoveMissingPayloads, { savedConfig }));
    websocketTopicMocks.onOpen?.();

    await user.click(screen.getByRole("button", { name: "Dry Run" }));
    expect(screen.getByRole("button", { name: "Dry Run" })).toHaveProperty("disabled", true);

    websocketTopicMocks.setProgress?.("Dry Run - Done. Identified 1 missing payload.");
    await waitFor(() => {
      expect(screen.getByRole("button", { name: "Dry Run" })).toHaveProperty("disabled", true);
    });
    expect(screen.getByRole("button", { name: "Running..." })).toHaveProperty("disabled", true);

    resolveResponse?.(jsonResponse({ status: true, message: "Done.", previewToken: "token" }));
    await waitFor(() => {
      expect(screen.getByRole("button", { name: "Dry Run" })).toHaveProperty("disabled", false);
    });
  });

  it("preserves the terminal audit link across a websocket disconnect", async () => {
    fetchMock.mockResolvedValueOnce(
      jsonResponse({ status: true, message: "Done.", previewToken: "token" }),
    );
    const user = userEvent.setup();
    render(createElement(RemoveMissingPayloads, { savedConfig }));
    websocketTopicMocks.onOpen?.();

    await user.click(screen.getByRole("button", { name: "Dry Run" }));
    await waitFor(() => {
      expect(screen.getByText(/Dry Run - Done/)).toBeTruthy();
    });
    expect(screen.getByRole("link", { name: "View audit" })).toBeTruthy();

    websocketTopicMocks.onClose?.();

    expect(screen.getByText(/Dry Run - Done/)).toBeTruthy();
    expect(screen.getByRole("link", { name: "View audit" })).toBeTruthy();
  });
});
