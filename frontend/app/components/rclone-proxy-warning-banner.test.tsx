// @vitest-environment jsdom

import { act, cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import {
  RcloneProxyWarningBanner,
  RCLONE_PROXY_STATUS_POLL_MS,
} from "./rclone-proxy-warning-banner";

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe("RcloneProxyWarningBanner", () => {
  it("shows persistent direct-backend guidance after proxied rclone traffic", () => {
    render(<RcloneProxyWarningBanner active />);

    expect(screen.getByText("rclone is using the frontend proxy")).toBeTruthy();
    expect(screen.getByText(/backend port/).textContent).toContain("8080");
    expect(screen.getByText(/backend port/).textContent).toContain("trusted Docker network");
    expect(screen.queryByRole("button")).toBeNull();
  });

  it("renders nothing before a detection", () => {
    const { container } = render(<RcloneProxyWarningBanner active={false} />);

    expect(container.innerHTML).toBe("");
  });

  it("synchronizes with root-loader revalidation without waiting for the poll", () => {
    const { rerender } = render(<RcloneProxyWarningBanner active={false} />);
    expect(screen.queryByText("rclone is using the frontend proxy")).toBeNull();

    rerender(<RcloneProxyWarningBanner active />);
    expect(screen.getByText("rclone is using the frontend proxy")).toBeTruthy();

    rerender(<RcloneProxyWarningBanner active={false} />);
    expect(screen.queryByText("rclone is using the frontend proxy")).toBeNull();
  });

  it("ignores a delayed poll response after root-loader revalidation", async () => {
    vi.useFakeTimers();
    let resolveStatus:
      ((value: { ok: true; json: () => Promise<{ active: boolean }> }) => void) | undefined;
    vi.stubGlobal(
      "fetch",
      vi.fn(
        () =>
          new Promise<{ ok: true; json: () => Promise<{ active: boolean }> }>((resolve) => {
            resolveStatus = resolve;
          }),
      ),
    );
    const { rerender } = render(<RcloneProxyWarningBanner active />);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(RCLONE_PROXY_STATUS_POLL_MS);
    });
    rerender(<RcloneProxyWarningBanner active={false} />);

    await act(async () => {
      if (!resolveStatus) throw new Error("Expected a pending status request");
      resolveStatus({ ok: true, json: () => Promise.resolve({ active: true }) });
      await Promise.resolve();
    });

    expect(screen.queryByText("rclone is using the frontend proxy")).toBeNull();
  });

  it("activates and clears from the lightweight status poll", async () => {
    vi.useFakeTimers();
    const states = [true, false];
    vi.stubGlobal(
      "fetch",
      vi.fn(() =>
        Promise.resolve({
          ok: true,
          json: () => Promise.resolve({ active: states.shift() }),
        }),
      ),
    );
    render(<RcloneProxyWarningBanner active={false} />);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(RCLONE_PROXY_STATUS_POLL_MS);
    });
    expect(screen.getByText("rclone is using the frontend proxy")).toBeTruthy();

    await act(async () => {
      await vi.advanceTimersByTimeAsync(RCLONE_PROXY_STATUS_POLL_MS);
    });
    expect(screen.queryByText("rclone is using the frontend proxy")).toBeNull();
  });
});
