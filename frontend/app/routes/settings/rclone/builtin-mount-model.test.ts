import { describe, expect, it } from "vitest";
import {
  coversSymlinkRoot,
  describeMountState,
  formatBytes,
  hoursToTimeSpan,
  mountsMatchServer,
  parseMounts,
  parseSize,
  serializeMounts,
  timeSpanToHours,
  upsertMount,
  newMount,
  removeMount,
  type BuiltinMount,
} from "./builtin-mount-model";

const mount = (overrides: Partial<BuiltinMount> = {}): BuiltinMount => ({
  Id: "library",
  MountPoint: "/mnt/remote/infinidysk",
  RemotePath: "/",
  Enabled: true,
  VfsCacheMode: "full",
  AllowOther: true,
  Links: true,
  ...overrides,
});

describe("parseMounts", () => {
  it("reads the configured mount list", () => {
    const mounts = parseMounts('[{"Id":"library","MountPoint":"/mnt/remote"}]');

    expect(mounts).toHaveLength(1);
    expect(mounts[0]!.MountPoint).toBe("/mnt/remote");
  });

  it("treats an empty value as no mounts", () => {
    expect(parseMounts("")).toEqual([]);
    expect(parseMounts(undefined)).toEqual([]);
  });

  // A malformed value must not blank the settings page; the operator needs to
  // see the rest of the tab to fix it.
  it("treats an unreadable value as no mounts", () => {
    expect(parseMounts("not json")).toEqual([]);
  });

  it("defaults the fields the backend fills in", () => {
    const [parsed] = parseMounts('[{"Id":"library","MountPoint":"/mnt/remote"}]');

    expect(parsed!.Enabled).toBe(true);
    expect(parsed!.RemotePath).toBe("/");
    expect(parsed!.VfsCacheMode).toBe("full");
  });
});

describe("serializeMounts", () => {
  it("round-trips through parseMounts", () => {
    const mounts = [mount()];

    expect(parseMounts(serializeMounts(mounts))).toEqual(mounts);
  });

  it("writes an empty list as an empty array rather than an empty string", () => {
    expect(serializeMounts([])).toBe("[]");
  });
});

describe("upsertMount", () => {
  it("adds a mount that is not in the list", () => {
    expect(upsertMount([], mount())).toHaveLength(1);
  });

  it("replaces a mount with the same id instead of duplicating it", () => {
    const updated = upsertMount([mount()], mount({ MountPoint: "/mnt/changed" }));

    expect(updated).toHaveLength(1);
    expect(updated[0]!.MountPoint).toBe("/mnt/changed");
  });

  it("keeps the position of the mount it replaces", () => {
    const list = [mount({ Id: "first" }), mount({ Id: "second" }), mount({ Id: "third" })];

    const updated = upsertMount(list, mount({ Id: "second", MountPoint: "/mnt/changed" }));

    expect(updated.map((m) => m.Id)).toEqual(["first", "second", "third"]);
  });
});

describe("removeMount", () => {
  it("removes only the named mount", () => {
    const list = [mount({ Id: "first" }), mount({ Id: "second" })];

    expect(removeMount(list, "first").map((m) => m.Id)).toEqual(["second"]);
  });
});

describe("describeMountState", () => {
  it("reports a live mount as mounted", () => {
    expect(describeMountState({ configured: true, enabled: true, mounted: true })).toEqual({
      label: "Mounted",
      variant: "success",
    });
  });

  it("reports a mount that is on but not yet live as starting", () => {
    expect(describeMountState({ configured: true, enabled: true, mounted: false })).toEqual({
      label: "Not mounted",
      variant: "warning",
    });
  });

  it("reports a switched-off mount as off rather than broken", () => {
    expect(describeMountState({ configured: true, enabled: false, mounted: false })).toEqual({
      label: "Off",
      variant: "neutral",
    });
  });

  // Mounted but no longer in the configuration: the next apply removes it, and
  // the operator should know that before it disappears.
  it("flags a mount that config no longer describes", () => {
    expect(describeMountState({ configured: false, enabled: false, mounted: true })).toEqual({
      label: "Unmanaged",
      variant: "warning",
    });
  });
});

describe("newMount", () => {
  // Symlink imports resolve through rclone.mount-dir, so that is the only path
  // a first mount should default to. A generic placeholder invites a typo that
  // silently breaks every imported file.
  it("defaults the first mount to where symlink imports expect it", () => {
    expect(newMount(0, "/data/nzbdav").MountPoint).toBe("/data/nzbdav");
  });

  it("falls back to a placeholder when no mount directory is configured", () => {
    expect(newMount(0, undefined).MountPoint).toBe("/mnt/remote/infinidysk");
  });

  it("gives additional mounts distinct ids", () => {
    expect(newMount(0, "/data/nzbdav").Id).not.toBe(newMount(1, "/data/nzbdav").Id);
  });
});

describe("coversSymlinkRoot", () => {
  const at = (mountPoint: string, enabled = true): BuiltinMount => ({
    ...mount(),
    MountPoint: mountPoint,
    Enabled: enabled,
  });

  it("is satisfied by an enabled mount on the symlink root", () => {
    expect(coversSymlinkRoot([at("/data/nzbdav")], "/data/nzbdav")).toBe(true);
  });

  it("ignores a trailing slash difference", () => {
    expect(coversSymlinkRoot([at("/data/nzbdav/")], "/data/nzbdav")).toBe(true);
  });

  it("is not satisfied by a mount somewhere else", () => {
    expect(coversSymlinkRoot([at("/mnt/other")], "/data/nzbdav")).toBe(false);
  });

  // A disabled mount is not applied, so it cannot be what keeps a library working.
  it("is not satisfied by a disabled mount on the right path", () => {
    expect(coversSymlinkRoot([at("/data/nzbdav", false)], "/data/nzbdav")).toBe(false);
  });

  it("does not warn when no mount directory is configured", () => {
    expect(coversSymlinkRoot([], undefined)).toBe(true);
  });
});

describe("parseMounts entry filtering", () => {
  // A null or half-written entry cannot be applied, collides with other
  // incomplete entries in upsertMount because their ids are both undefined, and
  // renders under a duplicate React key.
  it("drops entries that are not usable mounts", () => {
    const mounts = parseMounts(
      '[null, {"Id":"library","MountPoint":"/mnt/remote"}, {"Id":"no-path"}, {"MountPoint":"/mnt/no-id"}, "nonsense"]',
    );

    expect(mounts).toHaveLength(1);
    expect(mounts[0]!.Id).toBe("library");
  });

  it("drops entries whose id or mount point is blank", () => {
    expect(parseMounts('[{"Id":"  ","MountPoint":"/mnt/remote"}]')).toEqual([]);
    expect(parseMounts('[{"Id":"library","MountPoint":"   "}]')).toEqual([]);
  });
});

describe("coversSymlinkRoot", () => {
  // A mount higher up the tree serves everything beneath it, so warning about
  // this setup told the operator their working library was broken.
  it("counts a parent mount as covering the symlink root", () => {
    const mounts = parseMounts('[{"Id":"library","MountPoint":"/mnt/remote"}]');

    expect(coversSymlinkRoot(mounts, "/mnt/remote/infinidysk")).toBe(true);
  });

  it("does not count a sibling directory as covering it", () => {
    const mounts = parseMounts('[{"Id":"library","MountPoint":"/mnt/remote-other"}]');

    expect(coversSymlinkRoot(mounts, "/mnt/remote/infinidysk")).toBe(false);
  });

  it("ignores mounts that are switched off", () => {
    const mounts = parseMounts('[{"Id":"library","MountPoint":"/mnt/remote","Enabled":false}]');

    expect(coversSymlinkRoot(mounts, "/mnt/remote")).toBe(false);
  });
});

describe("duration round-tripping", () => {
  // .NET's TimeSpan rejects an hour component above 23, so 24 hours has to move
  // into the day component.
  it("moves whole days out of the hour component", () => {
    expect(hoursToTimeSpan(24)).toBe("1.00:00:00");
    expect(hoursToTimeSpan(36)).toBe("1.12:00:00");
    expect(hoursToTimeSpan(6)).toBe("06:00:00");
  });

  it("reads a duration back into hours", () => {
    expect(timeSpanToHours("1.00:00:00", 0)).toBe(24);
    expect(timeSpanToHours("06:00:00", 0)).toBe(6);
    expect(timeSpanToHours(undefined, 24)).toBe(24);
    expect(timeSpanToHours("nonsense", 24)).toBe(24);
  });
});

describe("size parsing", () => {
  it("accepts rclone's suffixes", () => {
    expect(parseSize("512M")).toBe(512 * 1024 ** 2);
    expect(parseSize("20G")).toBe(20 * 1024 ** 3);
    expect(parseSize("1024")).toBe(1024);
  });

  it("treats an empty or unreadable value as unset", () => {
    expect(parseSize("")).toBeNull();
    expect(parseSize("later")).toBeNull();
  });

  it("formats sizes for display", () => {
    expect(formatBytes(20 * 1024 ** 3)).toBe("20 GB");
    expect(formatBytes(1536)).toBe("1.5 KB");
    expect(formatBytes(null)).toBe("unknown");
  });
});

describe("mountsMatchServer", () => {
  const saved = {
    id: "library",
    mountPoint: "/mnt/remote",
    remotePath: "/",
    vfsCacheMode: "full",
    configured: true,
    enabled: true,
  };

  it("matches when the edited mounts equal what the backend saved", () => {
    const mounts = parseMounts('[{"Id":"library","MountPoint":"/mnt/remote"}]');

    expect(mountsMatchServer(mounts, [saved])).toBe(true);
  });

  it("spots an edited field", () => {
    const mounts = parseMounts('[{"Id":"library","MountPoint":"/mnt/changed"}]');

    expect(mountsMatchServer(mounts, [saved])).toBe(false);
  });

  it("spots an added or removed mount", () => {
    expect(mountsMatchServer([], [saved])).toBe(false);
    expect(
      mountsMatchServer(
        parseMounts(
          '[{"Id":"library","MountPoint":"/mnt/remote"},{"Id":"b","MountPoint":"/mnt/b"}]',
        ),
        [saved],
      ),
    ).toBe(false);
  });

  it("ignores live mounts the configuration no longer describes", () => {
    const unmanaged = { ...saved, id: "/mnt/orphan", mountPoint: "/mnt/orphan", configured: false };
    const mounts = parseMounts('[{"Id":"library","MountPoint":"/mnt/remote"}]');

    expect(mountsMatchServer(mounts, [saved, unmanaged])).toBe(true);
  });

  // Before the first status response there is no basis to claim a difference.
  it("spots an edited read-ahead, cache age, or symlink toggle", () => {
    // These are editable in the tab, so leaving them out of the comparison
    // would let Apply run against settings that were never saved.
    const base = parseMounts('[{"Id":"library","MountPoint":"/mnt/remote"}]');

    expect(
      mountsMatchServer(
        base.map((m) => ({ ...m, ReadAheadBytes: 536870912 })),
        [saved],
      ),
    ).toBe(false);
    expect(
      mountsMatchServer(
        base.map((m) => ({ ...m, VfsCacheMaxAge: "06:00:00" })),
        [saved],
      ),
    ).toBe(false);
    expect(
      mountsMatchServer(
        base.map((m) => ({ ...m, Links: false })),
        [saved],
      ),
    ).toBe(false);
    expect(
      mountsMatchServer(
        base.map((m) => ({ ...m, DirCacheTime: "01:00:00" })),
        [saved],
      ),
    ).toBe(false);
    // An import is the only way this one changes, and it must not read as saved.
    expect(
      mountsMatchServer(
        base.map((m) => ({ ...m, VfsCacheMaxSizeBytes: 21474836480 })),
        [saved],
      ),
    ).toBe(false);
  });

  it("matches when those fields agree across the wire formats", () => {
    // The status endpoint reports seconds; the form holds a TimeSpan string.
    const mounts = parseMounts(
      '[{"Id":"library","MountPoint":"/mnt/remote","VfsCacheMaxAge":"06:00:00","DirCacheTime":"01:00:00","ReadAheadBytes":536870912,"Links":false}]',
    );

    expect(
      mountsMatchServer(mounts, [
        {
          ...saved,
          vfsCacheMaxAgeSeconds: 21600,
          dirCacheTimeSeconds: 3600,
          readAheadBytes: 536870912,
          links: false,
        },
      ]),
    ).toBe(true);
  });

  it("assumes a match until the status has been read", () => {
    expect(mountsMatchServer(parseMounts('[{"Id":"a","MountPoint":"/mnt/a"}]'), undefined)).toBe(
      true,
    );
  });
});
