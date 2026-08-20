import { describe, expect, it } from "vitest";
import {
  DEFAULT_SETTINGS_TAB,
  SETTINGS_TAB_GROUPS,
  getSettingsTabItem,
  parseSettingsTab,
  settingsPath,
} from "./settings-tabs";

describe("settings tabs", () => {
  it("groups tabs by user task in navigation order", () => {
    expect(
      SETTINGS_TAB_GROUPS.map((group) => ({
        title: group.title,
        tabs: group.items.map((item) => item.id),
      })),
    ).toEqual([
      { title: "Providers & Search", tabs: ["usenet", "indexers", "profiles"] },
      { title: "Queue & Import", tabs: ["queue", "sabnzbd"] },
      { title: "Playback & Files", tabs: ["streaming", "webdav"] },
      { title: "Automation", tabs: ["watchdog", "preflight", "watchtower", "warden"] },
      { title: "Integrations", tabs: ["arrs", "rclone"] },
      { title: "System", tabs: ["repairs", "maintenance", "backup", "support", "migration"] },
    ]);
  });

  it("parses the new Queue and Streaming tabs", () => {
    expect(parseSettingsTab("queue")).toBe("queue");
    expect(parseSettingsTab("streaming")).toBe("streaming");
  });

  it("falls back to Usenet for missing and unknown tabs", () => {
    expect(DEFAULT_SETTINGS_TAB).toBe("usenet");
    expect(parseSettingsTab(null)).toBe("usenet");
    expect(parseSettingsTab("unknown")).toBe("usenet");
  });

  it("builds stable settings paths", () => {
    expect(settingsPath()).toBe("/settings");
    expect(settingsPath("queue")).toBe("/settings?tab=queue");
    expect(settingsPath("streaming")).toBe("/settings?tab=streaming");
  });

  it("uses the discoverable Health & Repairs label without changing its id", () => {
    expect(getSettingsTabItem("repairs")).toMatchObject({
      id: "repairs",
      label: "Health & Repairs",
    });
  });

  it("uses an extensible Arr Apps label for current and future integrations", () => {
    expect(getSettingsTabItem("arrs")).toMatchObject({
      id: "arrs",
      label: "Arr Apps",
    });
  });
});
