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

  it("defaults a sidecar symlink setup to RC notifications on regardless of stored config", () => {
    const draft = createInitialDraft(
      {
        ...SETUP_DEFAULT_CONFIG,
        "rclone.builtin.enabled": "false",
        "rclone.rc-enabled": "false",
      },
      {},
      [],
    );

    expect(draft.config["rclone.rc-enabled"]).toBe("true");
    expect(draft.config["usenet.segment-cache.enabled"]).toBe("false");
  });

  it("re-enables RC notifications when switching to sidecar symlinks but leaves STRM untouched", () => {
    const strm = applyStrategy(
      {
        ...SETUP_DEFAULT_CONFIG,
        "api.import-strategy": "strm",
        "rclone.builtin.enabled": "false",
        "rclone.rc-enabled": "false",
      },
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
    const draft = createInitialDraft(
      { ...SETUP_DEFAULT_CONFIG, "rclone.builtin.enabled": "false" },
      {},
      ["manual"],
    );

    expect(completionSetupConfig(SETUP_DEFAULT_CONFIG, draft, {})).toMatchObject({
      "rclone.mount-dir": "/mnt/nzbdav",
      "rclone.rc-enabled": "true",
      "backup.schedule-enabled": "false",
      "media.library-dir": "",
    });
  });

  it("requires read-ahead confirmation and a valid RC host for a sidecar symlink setup", () => {
    const draft = createInitialDraft(
      { ...SETUP_DEFAULT_CONFIG, "rclone.builtin.enabled": "false" },
      {},
      ["manual"],
    );

    expect(validateSetupStep(1, draft, {}, false, "symlinks")).toEqual([
      "Enter a valid http(s) rclone RC host.",
      "Confirm that the rclone sidecar has VFS read-ahead enabled.",
    ]);
  });

  it("offers the built-in rclone first for a new symlink setup", () => {
    // Running rclone ourselves is the path that needs no second container, so
    // it is what a fresh install gets unless the operator says otherwise.
    const draft = createInitialDraft(SETUP_DEFAULT_CONFIG, {}, ["manual"]);

    expect(draft.config["rclone.builtin.enabled"]).toBe("true");
  });

  it("writes no built-in mount settings for a STRM library", () => {
    // STRM playback opens URLs directly, so there is nothing to mount and the
    // wizard has no business changing that setting in either direction.
    const draft = createInitialDraft(
      { ...SETUP_DEFAULT_CONFIG, "api.import-strategy": "strm" },
      {},
      ["manual"],
    );

    const config = completionSetupConfig(SETUP_DEFAULT_CONFIG, draft, {});

    expect("rclone.builtin.enabled" in config).toBe(false);
    expect("rclone.builtin.mounts" in config).toBe(false);
  });

  it("survives a symlinks to STRM and back round trip", () => {
    // A detour through STRM must not silently move the operator between the
    // built-in mount and their own rclone container, in either direction.
    const builtin = SETUP_DEFAULT_CONFIG;
    expect(
      applyStrategy(applyStrategy(builtin, "strm", {}), "symlinks", {})["rclone.builtin.enabled"],
    ).toBe("true");

    const sidecar = { ...SETUP_DEFAULT_CONFIG, "rclone.builtin.enabled": "false" };
    expect(
      applyStrategy(applyStrategy(sidecar, "strm", {}), "symlinks", {})["rclone.builtin.enabled"],
    ).toBe("false");
  });

  it("does not propose RC notifications when InfiniDysk runs rclone itself", () => {
    // Those settings address a separate rclone container. Switching them on for
    // the built-in daemon would ask for a host that does not exist.
    const builtin = applyStrategy(
      { ...SETUP_DEFAULT_CONFIG, "rclone.builtin.enabled": "true", "rclone.rc-enabled": "false" },
      "symlinks",
      {},
    );

    expect(builtin["rclone.rc-enabled"]).toBe("false");
  });

  it("derives the built-in mount from the mount directory on completion", () => {
    const draft = createInitialDraft(
      { ...SETUP_DEFAULT_CONFIG, "rclone.mount-dir": "/data/nzbdav" },
      {},
      ["manual"],
    );

    const config = completionSetupConfig(SETUP_DEFAULT_CONFIG, draft, {});

    expect(config["rclone.builtin.enabled"]).toBe("true");
    expect(JSON.parse(config["rclone.builtin.mounts"] ?? "[]")).toEqual([
      { Id: "library", MountPoint: "/data/nzbdav", RemotePath: "/", Enabled: true },
    ]);
  });

  it("asks only for the mount directory when InfiniDysk runs rclone itself", () => {
    // No sidecar means no RC host to reach and no flags for the operator to
    // confirm; asking for either would be asking about a container that is not
    // there.
    const draft = createInitialDraft(SETUP_DEFAULT_CONFIG, {}, ["manual"]);

    expect(validateSetupStep(1, draft, {}, false, "symlinks")).toEqual([]);
  });

  it("still wants the mount directory for a built-in setup", () => {
    const draft = createInitialDraft({ ...SETUP_DEFAULT_CONFIG, "rclone.mount-dir": "" }, {}, [
      "manual",
    ]);

    expect(validateSetupStep(1, draft, {}, false, "symlinks")).toEqual([
      "Enter the rclone mount directory.",
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
