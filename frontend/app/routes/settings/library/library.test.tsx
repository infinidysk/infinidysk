// @vitest-environment jsdom
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { useState } from "react";
import { LibrarySettings, selectedPlexServerIds } from "./library";

const { plexRequest } = vi.hoisted(() => ({ plexRequest: vi.fn() }));
vi.mock("../plex/plex-api", () => ({ plexRequest }));
afterEach(() => {
  cleanup();
  plexRequest.mockReset();
});

const initial = {
  "media.library-enabled": "true",
  "media.library-video-only": "false",
  "media.library-dir": "/mnt/media",
  "media.library-scan-dirs": "[]",
  "media.library-scan-interval-minutes": "15",
  "media.library-plex-server-ids": "",
};
function Harness() {
  const [config, setConfig] = useState<Record<string, string>>(initial);
  return (
    <>
      <LibrarySettings config={config} savedConfig={initial} setNewConfig={setConfig} />
      <output data-testid="config">{JSON.stringify(config)}</output>
    </>
  );
}

describe("Media Library settings", () => {
  it("lets operators enable video-only indexing", async () => {
    plexRequest.mockResolvedValue({
      servers: [],
      ready: false,
      syncedAt: null,
      entryCount: 0,
      warning: null,
      syncing: false,
    });
    const user = userEvent.setup();
    render(<Harness />);
    await user.click(screen.getByLabelText("Index video files only"));
    expect(screen.getByTestId("config").textContent).toContain('"media.library-video-only":"true"');
  });

  it("defaults source selection to every enabled server", () => {
    const servers = [
      { id: "home", enabled: true },
      { id: "old", enabled: false },
    ] as Parameters<typeof selectedPlexServerIds>[1];
    expect([...selectedPlexServerIds("", servers)]).toEqual(["home"]);
    expect([...selectedPlexServerIds("[]", servers)]).toEqual([]);
  });

  it("offers the 15-minute default and disables controls when the library is off", async () => {
    plexRequest.mockImplementation((operation: string) =>
      Promise.resolve(
        operation === "servers"
          ? { servers: [{ id: "home", name: "Home Plex", enabled: true }] }
          : { ready: true, syncedAt: null, entryCount: 12, warning: null, syncing: false },
      ),
    );
    const user = userEvent.setup();
    render(<Harness />);
    expect(screen.getByLabelText("Scan every")).toHaveProperty("value", "15");
    await screen.findByText("Home Plex");
    await user.click(screen.getByLabelText("Enable Media Library"));
    await waitFor(() =>
      expect(screen.getByLabelText("Scan every").matches(":disabled")).toBe(true),
    );
    expect(screen.getByTestId("config").textContent).toContain('"media.library-enabled":"false"');
  });

  it("saves an explicit empty Plex selection and waits for Save before syncing", async () => {
    plexRequest.mockImplementation((operation: string) =>
      Promise.resolve(
        operation === "servers"
          ? { servers: [{ id: "home", name: "Home Plex", enabled: true }] }
          : { ready: true, syncedAt: null, entryCount: 12, warning: null, syncing: false },
      ),
    );
    const user = userEvent.setup();
    render(<Harness />);
    const source = await screen.findByRole("checkbox", { name: "Home Plex" });
    expect(source).toHaveProperty("checked", true);
    await user.click(source);
    expect(screen.getByTestId("config").textContent).toContain(
      '"media.library-plex-server-ids":"[]"',
    );
    expect(screen.getByRole("button", { name: "Sync Plex now" }).matches(":disabled")).toBe(true);
  });

  it("adds and removes an additional scan directory", async () => {
    plexRequest.mockResolvedValue({
      servers: [],
      ready: false,
      syncedAt: null,
      entryCount: 0,
      warning: null,
      syncing: false,
    });
    const user = userEvent.setup();
    render(<Harness />);
    await user.type(screen.getByLabelText("Additional scan directories"), "/mnt/special2");
    await user.click(screen.getByRole("button", { name: "Add directory" }));
    expect(screen.getByTestId("config").textContent).toContain(
      '"media.library-scan-dirs":"[\\"/mnt/special2\\"]"',
    );
    await user.click(screen.getByRole("button", { name: "Remove" }));
    expect(screen.getByTestId("config").textContent).toContain('"media.library-scan-dirs":"[]"');
  });
});
