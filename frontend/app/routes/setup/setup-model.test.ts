import { describe, expect, it } from "vitest";
import {
  SETUP_DEFAULT_CONFIG,
  applyStrategy,
  changedSetupConfig,
  completionSetupConfig,
  createInitialDraft,
  safeReturnTo,
  validateSetupStep,
} from "./setup-model";

describe("setup model", () => {
  it("derives segment cache from the final library strategy", () => {
    const baseline = { ...SETUP_DEFAULT_CONFIG, "usenet.segment-cache.enabled": "true" };

    const symlinks = applyStrategy(baseline, "symlinks", {});
    expect(symlinks["usenet.segment-cache.enabled"]).toBe("false");

    const strm = applyStrategy(symlinks, "strm", {});
    expect(strm["usenet.segment-cache.enabled"]).toBe("true");

    const symlinksAgain = applyStrategy(strm, "symlinks", {});
    expect(symlinksAgain["usenet.segment-cache.enabled"]).toBe("false");
  });

  it("preserves environment-managed values and omits them from the patch", () => {
    const baseline = { ...SETUP_DEFAULT_CONFIG, "usenet.segment-cache.enabled": "true" };
    const managed = {
      "usenet.segment-cache.enabled": "NZBDAV_CONFIG__USENET__SEGMENT_CACHE__ENABLED",
    };
    const draft = applyStrategy(baseline, "symlinks", managed);

    expect(draft["usenet.segment-cache.enabled"]).toBe("true");
    expect(changedSetupConfig(baseline, draft, managed)).toEqual({});
  });

  it("defaults pending symlink setup to RC notifications on", () => {
    const draft = createInitialDraft(SETUP_DEFAULT_CONFIG, {}, [], true);

    expect(draft.config["rclone.rc-enabled"]).toBe("true");
    expect(draft.config["usenet.segment-cache.enabled"]).toBe("false");
  });

  it("includes selected branch defaults in the completion payload", () => {
    const draft = createInitialDraft(SETUP_DEFAULT_CONFIG, {}, ["manual"], false);

    expect(completionSetupConfig(SETUP_DEFAULT_CONFIG, draft, {})).toMatchObject({
      "rclone.mount-dir": "/mnt/nzbdav",
      "rclone.rc-enabled": "false",
      "backup.schedule-enabled": "false",
      "media.library-dir": "",
    });
  });

  it("requires read-ahead confirmation and a valid RC host for symlinks", () => {
    const draft = createInitialDraft(SETUP_DEFAULT_CONFIG, {}, ["manual"], true);

    expect(validateSetupStep(1, draft, {}, false, "symlinks")).toEqual([
      "Enter a valid http(s) rclone RC host.",
      "Confirm that the rclone sidecar has VFS read-ahead enabled.",
    ]);
  });

  it.each([
    [null, "/overview"],
    ["https://example.com", "/overview"],
    ["//example.com", "/overview"],
    ["/setup?again=1", "/overview"],
    ["/queue?page=2", "/queue?page=2"],
  ])("normalizes return target %s", (value, expected) => {
    expect(safeReturnTo(value)).toBe(expected);
  });
});
