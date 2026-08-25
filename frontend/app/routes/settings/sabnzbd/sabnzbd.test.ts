import { describe, expect, it } from "vitest";
import { isSabnzbdSettingsUpdated, isSabnzbdSettingsValid } from "./sabnzbd";

const config = {
  "api.categories": "tv,movies",
  "api.nzb-backup-enabled": "false",
  "api.nzb-backup-location": "",
  "api.import-strategy": "symlinks",
  "api.completed-downloads-dir": "",
  "general.base-url": "",
};

describe("SABnzbd import strategy", () => {
  it("keeps symlink installs valid without STRM paths", () => {
    expect(isSabnzbdSettingsValid(config)).toBe(true);
  });

  it("requires a completed directory and base URL when STRM is selected", () => {
    expect(
      isSabnzbdSettingsValid({
        ...config,
        "api.import-strategy": "strm",
      }),
    ).toBe(false);

    expect(
      isSabnzbdSettingsValid({
        ...config,
        "api.import-strategy": "strm",
        "api.completed-downloads-dir": "/mnt/jellyfin",
        "general.base-url": "https://infinidysk.example",
      }),
    ).toBe(true);
  });

  it("tracks import-strategy as a saveable change", () => {
    expect(isSabnzbdSettingsUpdated(config, { ...config, "api.import-strategy": "strm" })).toBe(
      true,
    );
  });
});
