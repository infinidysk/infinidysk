import { describe, expect, it } from "vitest";
import { isSabnzbdSettingsUpdated, isSabnzbdSettingsValid } from "./sabnzbd";

const config = {
  "api.categories": "tv,movies",
  "api.nzb-backup-enabled": "false",
  "api.nzb-backup-location": "",
  "api.import-strategy": "symlinks",
  "api.symlink-output-enabled": "",
  "api.symlink-output-dir": "",
  "api.strm-output-enabled": "",
  "api.completed-downloads-dir": "",
  "general.base-url": "",
};

describe("SABnzbd dual import outputs", () => {
  it("keeps legacy symlink installs valid without explicit output toggles", () => {
    expect(isSabnzbdSettingsValid(config)).toBe(true);
  });

  it("requires a completed directory and base URL when STRM is enabled", () => {
    expect(
      isSabnzbdSettingsValid({
        ...config,
        "api.strm-output-enabled": "true",
      }),
    ).toBe(false);

    expect(
      isSabnzbdSettingsValid({
        ...config,
        "api.strm-output-enabled": "true",
        "api.completed-downloads-dir": "/mnt/jellyfin",
        "general.base-url": "https://infinidysk.example",
      }),
    ).toBe(true);
  });

  it("tracks every dual-output setting as a saveable change", () => {
    for (const key of [
      "api.symlink-output-enabled",
      "api.symlink-output-dir",
      "api.strm-output-enabled",
    ]) {
      expect(isSabnzbdSettingsUpdated(config, { ...config, [key]: "true" })).toBe(true);
    }
  });
});
