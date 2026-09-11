// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { createElement, useEffect, useState } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { BuiltinMountSettings, isBuiltinMountSettingsUpdated } from "./builtin-mount";

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

/**
 * A Response body can only be read once, so one instance shared across calls
 * throws on the second status poll. Every invocation gets a fresh response.
 */
function respondWith(body: unknown) {
  fetchMock.mockImplementation(() => Promise.resolve(jsonResponse(body)));
}

/** fetch accepts a string, a URL, or a Request; only the path matters here. */
function requestUrl(input: RequestInfo | URL): string {
  if (typeof input === "string") return input;
  return input instanceof URL ? input.href : input.url;
}

function Harness({ initial }: { initial: Record<string, string> }) {
  const [config, setNewConfig] = useState(initial);
  return createElement(BuiltinMountSettings, { config, setNewConfig });
}

/** Same, but reports what the page saved so a test can assert on it. */
function RecordingHarness({
  initial,
  onChange,
}: {
  initial: Record<string, string>;
  onChange: (config: Record<string, string>) => void;
}) {
  const [config, setNewConfig] = useState(initial);
  useEffect(() => onChange(config), [config, onChange]);
  return createElement(BuiltinMountSettings, { config, setNewConfig });
}

const enabledConfig = {
  "rclone.builtin.enabled": "true",
  "rclone.builtin.mounts": '[{"Id":"library","MountPoint":"/mnt/remote/infinidysk"}]',
};

/** The status row the backend reports for `enabledConfig` once it is saved. */
const savedRow = {
  id: "library",
  mountPoint: "/mnt/remote/infinidysk",
  remotePath: "/",
  vfsCacheMode: "full",
  configured: true,
  enabled: true,
  mounted: false,
};

describe("BuiltinMountSettings", () => {
  it("stays out of the way until built-in mode is switched on", () => {
    render(createElement(Harness, { initial: { "rclone.builtin.enabled": "false" } }));

    expect(screen.getByText("Run rclone inside InfiniDysk")).toBeTruthy();
    expect(screen.queryByText("Mounts")).toBeNull();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  // The most important screen in this feature: the container is missing its FUSE
  // flags, which no amount of correct configuration can fix.
  it("shows the exact compose changes when the container cannot mount", async () => {
    respondWith({
      status: true,
      enabled: true,
      running: false,
      blockingReasons: ["/dev/fuse is not available inside the container."],
      mounts: [],
    });

    render(createElement(Harness, { initial: enabledConfig }));

    await waitFor(() => {
      expect(screen.getByText("This container cannot mount yet")).toBeTruthy();
    });

    expect(screen.getByText(/\/dev\/fuse is not available/)).toBeTruthy();
    expect(screen.getByText(/SYS_ADMIN/)).toBeTruthy();
    // Mentioned both in the compose block and in the note explaining why it matters.
    expect(screen.getAllByText(/rshared/).length).toBeGreaterThan(0);
  });

  // The gap this closes: a fresh install with no rclone to import from could
  // start the daemon and then never mount, with nothing in the UI to fix it.
  it("asks for the WebDAV password when no remote exists yet", async () => {
    respondWith({
      status: true,
      enabled: true,
      running: true,
      remoteConfigured: false,
      mounts: [],
    });

    render(createElement(Harness, { initial: enabledConfig }));

    await waitFor(() => expect(screen.getByText("Connect to your library")).toBeTruthy());
    expect(screen.getByText("WebDAV password")).toBeTruthy();
    expect(screen.getByText("Connect and mount")).toBeTruthy();
  });

  it("offers to reconnect once the remote exists", async () => {
    // Rclone authenticated when the mount was made and keeps using what it had,
    // so a rotated WebDAV password needs entering again. Hiding the form once a
    // remote exists left no way to do that: the remote and the mount table both
    // look healthy while every read fails.
    respondWith({
      status: true,
      enabled: true,
      running: true,
      remoteConfigured: true,
      mounts: [],
    });

    render(createElement(Harness, { initial: enabledConfig }));

    await waitFor(() => expect(screen.getByText("Mounts")).toBeTruthy());
    expect(screen.queryByText("Connect to your library")).toBeNull();
    expect(screen.getByText("Reconnect to your library")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Reconnect and remount" })).toBeTruthy();
  });

  it("reports a configured mount that is not mounted yet", async () => {
    respondWith({
      status: true,
      enabled: true,
      running: true,
      remoteConfigured: true,
      mounts: [
        {
          id: "library",
          mountPoint: "/mnt/remote/infinidysk",
          remotePath: "/",
          vfsCacheMode: "full",
          configured: true,
          enabled: true,
          mounted: false,
        },
      ],
    });

    render(createElement(Harness, { initial: enabledConfig }));

    await waitFor(() => expect(screen.getByText("Daemon running")).toBeTruthy());
    expect(screen.getByText("Not mounted")).toBeTruthy();
  });

  it("warns that a mount missing from the list will be removed on apply", async () => {
    respondWith({
      status: true,
      enabled: true,
      running: true,
      remoteConfigured: true,
      mounts: [
        {
          id: "/mnt/orphan",
          mountPoint: "/mnt/orphan",
          remotePath: "/",
          vfsCacheMode: "unknown",
          configured: false,
          enabled: false,
          mounted: true,
        },
      ],
    });

    render(createElement(Harness, { initial: enabledConfig }));

    await waitFor(() => {
      expect(screen.getByText(/is mounted but is no longer in this list/)).toBeTruthy();
    });
  });

  it("offers a first mount at the folder imports already resolve through", async () => {
    respondWith({
      status: true,
      enabled: true,
      running: true,
      remoteConfigured: true,
      symlinkMountDir: "/data/nzbdav",
      mounts: [],
    });

    render(
      createElement(Harness, {
        initial: { "rclone.builtin.enabled": "true", "rclone.builtin.mounts": "" },
      }),
    );

    await waitFor(() => expect(screen.getByText("Add a mount at /data/nzbdav")).toBeTruthy());
  });

  // Mounts that look healthy but miss the symlink root leave the library
  // unplayable, which is not something to discover at playback time.
  it("warns when no mount covers the folder imports resolve through", async () => {
    respondWith({
      status: true,
      enabled: true,
      running: true,
      remoteConfigured: true,
      symlinkMountDir: "/data/nzbdav",
      mounts: [],
    });

    render(
      createElement(Harness, {
        initial: {
          "rclone.builtin.enabled": "true",
          "rclone.builtin.mounts": '[{"Id":"other","MountPoint":"/mnt/elsewhere"}]',
        },
      }),
    );

    await waitFor(() => {
      expect(screen.getByText(/imports will not play until a mount covers it/)).toBeTruthy();
    });
  });

  it("does not warn when a mount covers the symlink root", async () => {
    respondWith({
      status: true,
      enabled: true,
      running: true,
      remoteConfigured: true,
      symlinkMountDir: "/data/nzbdav",
      mounts: [],
    });

    render(
      createElement(Harness, {
        initial: {
          "rclone.builtin.enabled": "true",
          "rclone.builtin.mounts": '[{"Id":"library","MountPoint":"/data/nzbdav"}]',
        },
      }),
    );

    await waitFor(() => expect(screen.getByText("Mounts")).toBeTruthy());
    expect(screen.queryByText(/will not play until a mount covers it/)).toBeNull();
  });

  // Pressing it without an external rclone configured can only fail, so it is
  // not offered as an action.
  it("does not offer to read an rclone server when none is configured", async () => {
    respondWith({
      status: true,
      enabled: true,
      running: true,
      remoteConfigured: true,
      mounts: [],
    });

    render(createElement(Harness, { initial: enabledConfig }));

    await waitFor(() => expect(screen.getByText("Move from your rclone container")).toBeTruthy());
    expect(screen.queryByText("Read my rclone server")).toBeNull();
    expect(screen.getByText(/No rclone server is configured/)).toBeTruthy();
  });

  it("offers to read an rclone server once one is configured", async () => {
    respondWith({
      status: true,
      enabled: true,
      running: true,
      remoteConfigured: true,
      mounts: [],
    });

    render(
      createElement(Harness, {
        initial: { ...enabledConfig, "rclone.host": "http://nzbdav_rclone:5572" },
      }),
    );

    await waitFor(() => expect(screen.getByText("Read my rclone server")).toBeTruthy());
  });

  it("explains that the mount path is preserved when moving from a container", async () => {
    respondWith({
      status: true,
      enabled: true,
      running: true,
      remoteConfigured: true,
      mounts: [],
    });

    render(
      createElement(Harness, {
        initial: { ...enabledConfig, "rclone.host": "http://nzbdav_rclone:5572" },
      }),
    );

    await waitFor(() => expect(screen.getByText("Move from your rclone container")).toBeTruthy());
    expect(screen.getByText(/symlinks point through/)).toBeTruthy();
  });

  // The password cannot be carried across, so the tab must not imply it can.
  it("does not claim the WebDAV password comes across with an import", async () => {
    respondWith({
      status: true,
      enabled: true,
      running: true,
      remoteConfigured: true,
      mounts: [],
    });

    render(
      createElement(Harness, {
        initial: { ...enabledConfig, "rclone.host": "http://nzbdav_rclone:5572" },
      }),
    );

    await waitFor(() => expect(screen.getByText("Read my rclone server")).toBeTruthy());
    expect(screen.getByText(/password is not copied/)).toBeTruthy();
  });

  // Apply reconciles the saved configuration, so applying with edits on screen
  // would report success for settings that were never applied.
  it("blocks Apply while the mount list has unsaved changes", async () => {
    respondWith({
      status: true,
      enabled: true,
      running: true,
      remoteConfigured: true,
      mounts: [],
    });

    render(createElement(Harness, { initial: enabledConfig }));

    await waitFor(() => expect(screen.getByText(/Save your changes first/)).toBeTruthy());
    expect(screen.getByText("Apply now").closest("button")!.disabled).toBe(true);
  });

  it("allows Apply once the mounts match what the backend has saved", async () => {
    respondWith({
      status: true,
      enabled: true,
      running: true,
      remoteConfigured: true,
      mounts: [savedRow],
    });

    render(createElement(Harness, { initial: enabledConfig }));

    await waitFor(() => expect(screen.getByText("Apply now")).toBeTruthy());
    expect(screen.queryByText(/Save your changes first/)).toBeNull();
    expect(screen.getByText("Apply now").closest("button")!.disabled).toBe(false);
  });

  it("shows where the cache lives and how much room it has", async () => {
    respondWith({
      status: true,
      enabled: true,
      running: true,
      remoteConfigured: true,
      cacheDir: "/config/rclone/cache",
      cacheDirFreeBytes: 40 * 1024 ** 3,
      cacheSizeLimitBytes: 20 * 1024 ** 3,
      mounts: [],
    });

    render(createElement(Harness, { initial: enabledConfig }));

    await waitFor(() => expect(screen.getByText(/\/config\/rclone\/cache/)).toBeTruthy());
    expect(screen.getByText(/40 GB free/)).toBeTruthy();
    expect(screen.getByText(/limit 20 GB/)).toBeTruthy();
  });

  it("lets a share's folder-listing cache be edited", async () => {
    // The tradeoff this hid behind was settled once InfiniDysk started dropping
    // cached folders itself, so it belongs in the tab like the other cache
    // settings.
    respondWith({
      status: true,
      enabled: true,
      running: true,
      remoteConfigured: true,
      mounts: [savedRow],
    });

    let saved: Record<string, string> = {};
    render(
      createElement(RecordingHarness, {
        initial: enabledConfig,
        onChange: (config: Record<string, string>) => {
          saved = config;
        },
      }),
    );

    const field = await screen.findByLabelText(/remember folder listings for/i);
    fireEvent.change(field, { target: { value: "1" } });

    await waitFor(() =>
      expect(saved["rclone.builtin.mounts"]).toContain('"DirCacheTime":"01:00:00"'),
    );
  });

  it("treats an edited cache size limit as an unsaved change", () => {
    // Save is enabled from this comparison. A setting missing from it looks
    // saved the moment it is typed, and the value is silently dropped.
    const saved = { "rclone.builtin.enabled": "true" };

    expect(
      isBuiltinMountSettingsUpdated(saved, {
        ...saved,
        "rclone.builtin.cache-size-limit": String(8 * 1024 ** 3),
      }),
    ).toBe(true);
  });

  it("shows the cache directory and size limit without opening anything first", async () => {
    // Both decide how much disk rclone takes, which is the question an operator
    // opens this card to answer. Hiding them behind a disclosure meant nobody
    // found them.
    respondWith({
      status: true,
      enabled: true,
      running: true,
      remoteConfigured: true,
      cacheDir: "/config/rclone/cache",
      cacheDirFreeBytes: 40 * 1024 ** 3,
      cacheSizeLimitBytes: 20 * 1024 ** 3,
      mounts: [],
    });

    render(createElement(Harness, { initial: enabledConfig }));

    expect(await screen.findByText("Cache directory")).toBeTruthy();
    expect(screen.getByText("Cache size limit")).toBeTruthy();
  });

  it("saves a typed cache size limit in bytes", async () => {
    respondWith({
      status: true,
      enabled: true,
      running: true,
      remoteConfigured: true,
      cacheDir: "/config/rclone/cache",
      cacheDirFreeBytes: 40 * 1024 ** 3,
      cacheSizeLimitBytes: 20 * 1024 ** 3,
      mounts: [],
    });

    let saved: Record<string, string> = {};
    render(
      createElement(RecordingHarness, {
        initial: enabledConfig,
        onChange: (config: Record<string, string>) => {
          saved = config;
        },
      }),
    );

    const field = await screen.findByPlaceholderText(/automatic/i);
    fireEvent.blur(field, { target: { value: "8G" } });

    await waitFor(() =>
      expect(saved["rclone.builtin.cache-size-limit"]).toBe(String(8 * 1024 ** 3)),
    );
  });

  it("hands the cache size back to the automatic budget when the box is cleared", async () => {
    respondWith({
      status: true,
      enabled: true,
      running: true,
      remoteConfigured: true,
      cacheDir: "/config/rclone/cache",
      cacheDirFreeBytes: 40 * 1024 ** 3,
      cacheSizeLimitBytes: 20 * 1024 ** 3,
      mounts: [],
    });

    let saved: Record<string, string> = {};
    render(
      createElement(RecordingHarness, {
        initial: { ...enabledConfig, "rclone.builtin.cache-size-limit": String(8 * 1024 ** 3) },
        onChange: (config: Record<string, string>) => {
          saved = config;
        },
      }),
    );

    const field = await screen.findByPlaceholderText(/automatic/i);
    fireEvent.blur(field, { target: { value: "  " } });

    await waitFor(() => expect(saved["rclone.builtin.cache-size-limit"]).toBe(""));
  });

  // A failed request used to become an unhandled rejection, leaving the button
  // spinning with nothing on screen.
  it("reports a failed apply instead of failing silently", async () => {
    fetchMock.mockImplementation((input) => {
      if (requestUrl(input).includes("/status")) {
        return Promise.resolve(
          jsonResponse({
            status: true,
            enabled: true,
            running: true,
            remoteConfigured: true,
            // Matches enabledConfig, so Apply is not blocked by unsaved changes.
            mounts: [savedRow],
          }),
        );
      }
      return Promise.reject(new Error("network down"));
    });

    render(createElement(Harness, { initial: enabledConfig }));

    await waitFor(() => expect(screen.getByText("Apply now")).toBeTruthy());
    screen.getByText("Apply now").click();

    await waitFor(() => expect(screen.getByText(/network down/)).toBeTruthy());
  });

  it("reports a failed import instead of failing silently", async () => {
    fetchMock.mockImplementation((input) => {
      if (requestUrl(input).includes("/status")) {
        return Promise.resolve(
          jsonResponse({
            status: true,
            enabled: true,
            running: true,
            remoteConfigured: true,
            mounts: [],
          }),
        );
      }
      return Promise.reject(new Error("connection refused"));
    });

    render(
      createElement(Harness, {
        initial: { ...enabledConfig, "rclone.host": "http://nzbdav_rclone:5572" },
      }),
    );

    await waitFor(() => expect(screen.getByText("Read my rclone server")).toBeTruthy());
    screen.getByText("Read my rclone server").click();

    await waitFor(() => expect(screen.getByText(/connection refused/)).toBeTruthy());
  });
});
