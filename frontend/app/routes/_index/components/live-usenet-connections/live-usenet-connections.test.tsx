// @vitest-environment jsdom
import { act, cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { LiveUsenetConnections } from "./live-usenet-connections";

const { useWebsocketTopicMock } = vi.hoisted(() => ({
  useWebsocketTopicMock: vi.fn(),
}));

vi.mock("~/utils/shared-websocket", () => ({
  useWebsocketTopic: useWebsocketTopicMock,
}));

afterEach(() => {
  cleanup();
  useWebsocketTopicMock.mockReset();
});

describe("LiveUsenetConnections", () => {
  it("shows a spinner only before the first cxs message", () => {
    let onMessage: ((message: string) => void) | undefined;
    useWebsocketTopicMock.mockImplementation(
      (_topic: string, _kind: string, handler: (message: string) => void) => {
        onMessage = handler;
      },
    );

    render(<LiveUsenetConnections hasUsenetProviders />);

    const widget = screen.getByLabelText("Usenet connections");
    expect(widget.querySelector(".loading-spinner")).not.toBeNull();
    expect(screen.getByText("Connecting")).toBeTruthy();

    act(() => {
      onMessage?.("0|1|1|3|20|2");
    });

    expect(screen.getByText("3/20")).toBeTruthy();
    expect(screen.getByText("1 active · 2 warm")).toBeTruthy();
    expect(widget.querySelector(".loading-spinner")).toBeNull();
    expect(screen.queryByText("Connecting")).toBeNull();

    act(() => {
      onMessage?.("0|2|1|4|20|2");
    });

    expect(screen.getByText("4/20")).toBeTruthy();
    expect(widget.querySelector(".loading-spinner")).toBeNull();
    expect(screen.queryByText("Connecting")).toBeNull();
  });

  it("shows a dash when no providers are configured", () => {
    render(<LiveUsenetConnections hasUsenetProviders={false} />);

    expect(screen.getByText("—")).toBeTruthy();
    expect(screen.getByText("No providers")).toBeTruthy();
    expect(
      screen.getByLabelText("Usenet connections").querySelector(".loading-spinner"),
    ).toBeNull();
    expect(useWebsocketTopicMock).toHaveBeenCalledWith(
      "cxs",
      "state",
      expect.any(Function),
      expect.objectContaining({ enabled: false }),
    );
  });
});
