// @vitest-environment jsdom
import { act, cleanup, render, screen, waitFor } from "@testing-library/react";
import { createElement, useState } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { RcloneSettings } from "./rclone";

const fetchMock = vi.fn<typeof fetch>();

beforeEach(() => {
  vi.stubGlobal("fetch", fetchMock);
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

const baseConfig: Record<string, string> = {
  "rclone.rc-enabled": "true",
  "rclone.host": "http://rclone:5572",
  "rclone.user": "",
  "rclone.pass": "",
  "usenet.segment-cache.enabled": "true",
};

let latestSetConfig: ((next: Record<string, string>) => void) | null = null;

function Harness({ initial }: { initial: Record<string, string> }) {
  const [config, setNewConfig] = useState(initial);
  latestSetConfig = (next) => setNewConfig(next);
  return createElement(RcloneSettings, { config, setNewConfig });
}

const successBody = {
  status: true,
  connected: true,
  readAheadBytes: 128 * 1024 * 1024,
};

async function flushPromises(): Promise<void> {
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 0));
  });
}

describe("RcloneSettings connection test", () => {
  it("shows the Segment Cache read-ahead warning after a successful test", async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse(successBody));
    render(createElement(Harness, { initial: baseConfig }));

    act(() => {
      screen.getByRole("button", { name: "Test Conn" }).click();
    });

    await waitFor(() => {
      expect(screen.getByText("Connection test successful")).toBeTruthy();
    });
    expect(screen.getByText(/VFS read-ahead enabled while Segment Cache/)).toBeTruthy();
  });

  it("ignores a stale test response when the host changes while the test is pending", async () => {
    let resolveFetch: ((response: Response) => void) | null = null;
    fetchMock.mockImplementationOnce(
      () =>
        new Promise<Response>((resolve) => {
          resolveFetch = resolve;
        }),
    );
    render(createElement(Harness, { initial: baseConfig }));

    act(() => {
      screen.getByRole("button", { name: "Test Conn" }).click();
    });
    expect(fetchMock).toHaveBeenCalledTimes(1);

    act(() => {
      latestSetConfig?.({ ...baseConfig, "rclone.host": "http://other-rclone:5572" });
    });

    act(() => {
      resolveFetch?.(jsonResponse(successBody));
    });
    await flushPromises();

    expect(screen.queryByText("Connection test successful")).toBeNull();
    expect(screen.queryByText(/VFS read-ahead enabled while Segment Cache/)).toBeNull();
    expect(screen.getByRole("button", { name: "Test Conn" })).toBeTruthy();
  });
});
