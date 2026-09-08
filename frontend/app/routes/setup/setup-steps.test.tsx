// @vitest-environment jsdom

import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ManagedEnvProvider } from "~/components/ui";
import { SETUP_DEFAULT_CONFIG, createInitialDraft } from "./setup-model";
import {
  BackupStep,
  IngestionStep,
  LibraryTypeStep,
  PlaybackStep,
  SetupProgress,
} from "./setup-steps";

afterEach(cleanup);

describe("setup wizard controls", () => {
  it("uses named radios for the exclusive library strategy", () => {
    const draft = createInitialDraft(SETUP_DEFAULT_CONFIG, {}, ["manual"]);
    render(
      <ManagedEnvProvider value={{}}>
        <LibraryTypeStep draft={draft} managedEnv={{}} updateDraft={vi.fn()} />
      </ManagedEnvProvider>,
    );

    const symlinks = screen.getByRole<HTMLInputElement>("radio", { name: "Symlinks · Plex" });
    const strm = screen.getByRole<HTMLInputElement>("radio", { name: "STRM · Emby/Jellyfin" });
    expect(symlinks.name).toBe("setup-library-strategy");
    expect(strm.name).toBe("setup-library-strategy");
    expect(symlinks.checked).toBe(true);
    const descriptionId = symlinks.getAttribute("aria-describedby");
    expect(descriptionId).toBe("setup-library-strategy-symlinks-description");
    expect(document.getElementById(descriptionId!)?.textContent).toBe(
      "Use an rclone mount and filesystem entries.",
    );
  });

  it("uses checkboxes for independent ingestion choices", () => {
    const draft = createInitialDraft(SETUP_DEFAULT_CONFIG, {}, ["search", "manual"]);
    render(
      <ManagedEnvProvider value={{}}>
        <IngestionStep draft={draft} updateDraft={vi.fn()} />
      </ManagedEnvProvider>,
    );

    expect(screen.getByRole("checkbox", { name: /Arr apps/ })).toBeTruthy();
    expect(
      screen.getByRole<HTMLInputElement>("checkbox", { name: /Built-in Search/ }).checked,
    ).toBe(true);
    expect(screen.getByRole<HTMLInputElement>("checkbox", { name: /Manual NZB/ }).checked).toBe(
      true,
    );
  });

  it("uses a toggle and disabled fieldset for an off backup schedule", () => {
    const draft = createInitialDraft(SETUP_DEFAULT_CONFIG, {}, ["manual"]);
    render(
      <ManagedEnvProvider value={{}}>
        <BackupStep draft={draft} updateDraft={vi.fn()} mainDatabaseProvider="sqlite" />
      </ManagedEnvProvider>,
    );

    expect(screen.getByRole("checkbox", { name: "Enable daily scheduled backups" })).toBeTruthy();
    expect(screen.getByRole("group", { name: "Backup schedule" })).toHaveProperty("disabled", true);
  });

  it("provides named desktop steps and mobile progress", () => {
    render(<SetupProgress step={2} />);

    expect(screen.getByLabelText("Setup progress")).toBeTruthy();
    expect(screen.getByRole("progressbar", { name: "Setup step 3 of 6" })).toHaveProperty(
      "value",
      3,
    );
    expect(screen.getByText("Step 3 of 6")).toBeTruthy();
  });

  it("offers to run rclone inside InfiniDysk by default", () => {
    // The built-in daemon needs no second container, so it is the path a new
    // install starts on, and none of the sidecar wiring applies to it.
    const draft = createInitialDraft(SETUP_DEFAULT_CONFIG, {}, ["manual"]);
    render(
      <ManagedEnvProvider value={{}}>
        <PlaybackStep draft={draft} updateDraft={vi.fn()} />
      </ManagedEnvProvider>,
    );

    expect(screen.getByRole("radio", { name: /run rclone inside infinidysk/i })).toHaveProperty(
      "checked",
      true,
    );
    expect(screen.queryByText("Rclone sidecar configuration")).toBeNull();
    expect(screen.queryByLabelText(/rclone rc host/i)).toBeNull();
  });

  it("shows rclone sidecar configuration expanded when the operator brings their own", () => {
    const draft = createInitialDraft(
      { ...SETUP_DEFAULT_CONFIG, "rclone.builtin.enabled": "false" },
      {},
      ["manual"],
    );
    render(
      <ManagedEnvProvider value={{}}>
        <PlaybackStep draft={draft} updateDraft={vi.fn()} />
      </ManagedEnvProvider>,
    );

    expect(screen.getByText("Rclone sidecar configuration").closest("details")).toHaveProperty(
      "open",
      true,
    );
    expect(screen.getByText("--poll-interval=0")).toBeTruthy();
    expect(screen.queryByText(/--vfs-read-chunk-/)).toBeNull();
    expect(screen.getByText("--vfs-read-ahead=512M")).toBeTruthy();
    expect(screen.getByText("--rc-addr=:5572")).toBeTruthy();
  });
});
