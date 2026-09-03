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
      "rclone.rc-enabled": "NZBDAV_CONFIG__RCLONE__RC_ENABLED",
    };
    const draft = applyStrategy(baseline, "symlinks", managed);

    expect(draft["usenet.segment-cache.enabled"]).toBe("true");
    expect(changedSetupConfig(baseline, draft, managed)).toEqual({});
  });

  it("defaults symlink setup to RC notifications on regardless of stored config", () => {
    const draft = createInitialDraft(
      { ...SETUP_DEFAULT_CONFIG, "rclone.rc-enabled": "false" },
      {},
      [],
    );

    expect(draft.config["rclone.rc-enabled"]).toBe("true");
    expect(draft.config["usenet.segment-cache.enabled"]).toBe("false");
  });

  it("re-enables RC notifications when switching to symlinks but leaves STRM untouched", () => {
    const strm = applyStrategy(
      { ...SETUP_DEFAULT_CONFIG, "api.import-strategy": "strm", "rclone.rc-enabled": "false" },
      "strm",
      {},
    );
    expect(strm["rclone.rc-enabled"]).toBe("false");

    expect(applyStrategy(strm, "symlinks", {})["rclone.rc-enabled"]).toBe("true");
  });

  it("keeps environment-managed RC notifications untouched", () => {
    const managed = { "rclone.rc-enabled": "NZBDAV_CONFIG__RCLONE__RC_ENABLED" };
    const draft = createInitialDraft(SETUP_DEFAULT_CONFIG, managed, []);

    expect(draft.config["rclone.rc-enabled"]).toBe("false");
  });

  it("includes selected branch defaults in the completion payload", () => {
    const draft = createInitialDraft(SETUP_DEFAULT_CONFIG, {}, ["manual"]);

    expect(completionSetupConfig(SETUP_DEFAULT_CONFIG, draft, {})).toMatchObject({
      "rclone.mount-dir": "/mnt/nzbdav",
      "rclone.rc-enabled": "true",
      "backup.schedule-enabled": "false",
      "media.library-dir": "",
    });
  });

  it("requires read-ahead confirmation and a valid RC host for symlinks", () => {
    const draft = createInitialDraft(SETUP_DEFAULT_CONFIG, {}, ["manual"]);

    expect(validateSetupStep(1, draft, {}, false, "symlinks")).toEqual([
      "Enter a valid http(s) rclone RC host.",
      "Confirm that the rclone sidecar has VFS read-ahead enabled.",
    ]);
  });

  it("allows an organized STRM library when no rclone mount is configured", () => {
    const draft = createInitialDraft(
      {
        ...SETUP_DEFAULT_CONFIG,
        "api.import-strategy": "strm",
        "rclone.mount-dir": "",
        "media.library-dir": "/mnt/media",
      },
      {},
      ["manual"],
    );

    expect(validateSetupStep(4, draft, {}, false, "strm")).toEqual([]);
  });

  it.each([
    [null, "/overview"],
    ["https://example.com", "/overview"],
    ["//example.com", "/overview"],
    ["/\\evil.example", "/overview"],
    ["/queue\\evil", "/overview"],
    [
      new URL("https://app.example/setup?returnTo=/%0A%2Fevil.example").searchParams.get(
        "returnTo",
      ),
      "/overview",
    ],
    ["/\t/evil.example", "/overview"],
    ["/\r/evil.example", "/overview"],
    ["/setup?again=1", "/overview"],
    ["/queue?page=2", "/queue?page=2"],
  ])("normalizes return target %s", (value, expected) => {
    expect(safeReturnTo(value)).toBe(expected);
  });
});
