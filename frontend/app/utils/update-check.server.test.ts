import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  checkForUpdate,
  compareSemver,
  isComparableVersion,
  resetUpdateCheckCache,
} from "./update-check.server";

const fetchMock = vi.fn<typeof fetch>();

vi.mock("./version.server", () => ({
  getBuildCommit: vi.fn(),
}));

import { getBuildCommit } from "./version.server";

const getBuildCommitMock = vi.mocked(getBuildCommit);

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

beforeEach(() => {
  vi.stubGlobal("fetch", fetchMock);
  resetUpdateCheckCache();
  getBuildCommitMock.mockReset();
  getBuildCommitMock.mockResolvedValue(undefined);
  vi.useFakeTimers();
  vi.setSystemTime(new Date("2026-07-12T12:00:00Z"));
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
  vi.clearAllMocks();
  resetUpdateCheckCache();
});

describe("compareSemver", () => {
  it.each([
    ["0.7.5", "0.7.5", 0],
    ["0.8.0", "0.7.5", 1],
    ["0.7.4", "0.7.5", -1],
    ["v0.8.0", "0.7.5", 1],
    ["0.7.5", "v0.8.0", -1],
    ["1.0.0", "0.9.9", 1],
    ["0.7", "0.7.0", 0],
  ])("compares %s and %s", (a, b, expectedSign) => {
    const result = compareSemver(a, b);
    if (expectedSign === 0) expect(result).toBe(0);
    else if (expectedSign > 0) expect(result).toBeGreaterThan(0);
    else expect(result).toBeLessThan(0);
  });
});

describe("isComparableVersion", () => {
  it.each([
    [undefined, false],
    [null, false],
    ["", false],
    ["unknown", false],
    ["Unknown", false],
    ["0.0.0", false],
    ["pre-123", false],
    ["PRE-1", false],
    ["dev-e0eef52", false],
    ["0.7.5", true],
    ["v0.7.5", true],
  ])("treats %s as comparable=%s", (version, expected) => {
    expect(isComparableVersion(version)).toBe(expected);
  });
});

describe("checkForUpdate", () => {
  it("returns null for non-comparable versions without a build commit", async () => {
    await expect(checkForUpdate("pre-42")).resolves.toBeNull();
    await expect(checkForUpdate("unknown")).resolves.toBeNull();
    await expect(checkForUpdate("0.0.0")).resolves.toBeNull();
    await expect(checkForUpdate(undefined)).resolves.toBeNull();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("returns update metadata when latest is newer", async () => {
    fetchMock.mockResolvedValueOnce(
      jsonResponse({
        tag_name: "v0.8.0",
        html_url: "https://github.com/nzbdav/nzbdav/releases/tag/v0.8.0",
      }),
    );

    await expect(checkForUpdate("0.7.5")).resolves.toEqual({
      kind: "release",
      latestVersion: "0.8.0",
      releaseUrl: "https://github.com/nzbdav/nzbdav/releases/tag/v0.8.0",
    });
  });

  it("returns null when current version is up to date", async () => {
    fetchMock.mockResolvedValueOnce(
      jsonResponse({
        tag_name: "v0.7.5",
        html_url: "https://github.com/nzbdav/nzbdav/releases/tag/v0.7.5",
      }),
    );

    await expect(checkForUpdate("0.7.5")).resolves.toBeNull();
  });

  it("returns null when current version is newer than latest", async () => {
    fetchMock.mockResolvedValueOnce(
      jsonResponse({
        tag_name: "v0.7.5",
        html_url: "https://github.com/nzbdav/nzbdav/releases/tag/v0.7.5",
      }),
    );

    await expect(checkForUpdate("0.8.0")).resolves.toBeNull();
  });

  it("falls back to releases page when html_url is missing", async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({ tag_name: "v0.9.0" }));

    await expect(checkForUpdate("0.7.5")).resolves.toEqual({
      kind: "release",
      latestVersion: "0.9.0",
      releaseUrl: "https://github.com/nzbdav/nzbdav/releases",
    });
  });

  it("returns null when fetch fails", async () => {
    fetchMock.mockRejectedValueOnce(new Error("network down"));
    await expect(checkForUpdate("0.7.5")).resolves.toBeNull();
  });

  it("returns null when GitHub responds with an error status", async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({ message: "rate limited" }, 403));
    await expect(checkForUpdate("0.7.5")).resolves.toBeNull();
  });

  it("reuses the cached release within the TTL", async () => {
    fetchMock.mockResolvedValueOnce(
      jsonResponse({
        tag_name: "v0.8.0",
        html_url: "https://github.com/nzbdav/nzbdav/releases/tag/v0.8.0",
      }),
    );

    await checkForUpdate("0.7.5");
    await checkForUpdate("0.7.5");

    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("refetches after the cache TTL expires", async () => {
    fetchMock
      .mockResolvedValueOnce(
        jsonResponse({
          tag_name: "v0.8.0",
          html_url: "https://github.com/nzbdav/nzbdav/releases/tag/v0.8.0",
        }),
      )
      .mockResolvedValueOnce(
        jsonResponse({
          tag_name: "v0.9.0",
          html_url: "https://github.com/nzbdav/nzbdav/releases/tag/v0.9.0",
        }),
      );

    await expect(checkForUpdate("0.7.5")).resolves.toMatchObject({
      latestVersion: "0.8.0",
    });

    vi.advanceTimersByTime(60 * 60 * 1000);

    await expect(checkForUpdate("0.7.5")).resolves.toMatchObject({
      latestVersion: "0.9.0",
    });
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it("never hits the compare endpoint when a newer release exists", async () => {
    getBuildCommitMock.mockResolvedValue({
      sha: "abc1234",
      branch: "main",
      source: "git",
    });
    fetchMock.mockResolvedValueOnce(
      jsonResponse({
        tag_name: "v0.8.0",
        html_url: "https://github.com/nzbdav/nzbdav/releases/tag/v0.8.0",
      }),
    );

    await checkForUpdate("0.7.5");

    expect(getBuildCommitMock).not.toHaveBeenCalled();
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(String(fetchMock.mock.calls[0]?.[0])).toContain("/releases/latest");
  });

  it("returns commits behind for an up-to-date source checkout on main", async () => {
    const buildSha = "abcdef0123456789abcdef0123456789abcdef01";
    getBuildCommitMock.mockResolvedValue({
      sha: buildSha,
      branch: "main",
      source: "git",
    });
    fetchMock
      .mockResolvedValueOnce(
        jsonResponse({
          tag_name: "v0.7.5",
          html_url: "https://github.com/nzbdav/nzbdav/releases/tag/v0.7.5",
        }),
      )
      .mockResolvedValueOnce(
        jsonResponse({
          status: "ahead",
          ahead_by: 2,
          html_url: `https://github.com/nzbdav/nzbdav/compare/${buildSha}...main`,
        }),
      );

    await expect(checkForUpdate("0.7.5")).resolves.toEqual({
      kind: "dev",
      commitsBehind: 2,
      compareUrl: `https://github.com/nzbdav/nzbdav/compare/${buildSha}...main`,
      trackRef: "main",
    });
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it("does not compare release images against unreleased main commits", async () => {
    getBuildCommitMock.mockResolvedValue({
      sha: "abcdef0123456789abcdef0123456789abcdef01",
      branch: "main",
      source: "env",
    });
    fetchMock.mockResolvedValueOnce(
      jsonResponse({
        tag_name: "v0.7.5",
        html_url: "https://github.com/nzbdav/nzbdav/releases/tag/v0.7.5",
      }),
    );

    await expect(checkForUpdate("0.7.5")).resolves.toBeNull();
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(String(fetchMock.mock.calls[0]?.[0])).toContain("/releases/latest");
  });

  it("does not compare release builds using version-embedded SHAs", async () => {
    getBuildCommitMock.mockResolvedValue({
      sha: "e0eef520",
      branch: "main",
      source: "version",
    });
    fetchMock.mockResolvedValueOnce(
      jsonResponse({
        tag_name: "v0.7.5",
        html_url: "https://github.com/nzbdav/nzbdav/releases/tag/v0.7.5",
      }),
    );

    await expect(checkForUpdate("0.7.5")).resolves.toBeNull();
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(String(fetchMock.mock.calls[0]?.[0])).toContain("/releases/latest");
  });

  it("notifies main-<sha> builds when main is ahead", async () => {
    getBuildCommitMock.mockResolvedValue({
      sha: "e0eef520",
      branch: "main",
      source: "version",
    });
    fetchMock.mockResolvedValueOnce(
      jsonResponse({
        status: "ahead",
        ahead_by: 2,
        html_url: "https://github.com/nzbdav/nzbdav/compare/e0eef520...main",
      }),
    );

    await expect(checkForUpdate("main-e0eef520")).resolves.toEqual({
      kind: "dev",
      commitsBehind: 2,
      compareUrl: "https://github.com/nzbdav/nzbdav/compare/e0eef520...main",
      trackRef: "main",
    });
    expect(getBuildCommitMock).toHaveBeenCalledWith({ version: "main-e0eef520" });
    expect(String(fetchMock.mock.calls[0]?.[0])).toContain("/compare/e0eef520...main");
  });
});

describe("checkForUpdate (dev builds)", () => {
  const buildSha = "abcdef0123456789abcdef0123456789abcdef01";

  beforeEach(() => {
    getBuildCommitMock.mockResolvedValue({
      sha: buildSha,
      branch: "dev",
      source: "env",
    });
  });

  it("returns commits behind for :dev images when the dev tag is ahead", async () => {
    fetchMock.mockResolvedValueOnce(
      jsonResponse({
        status: "ahead",
        ahead_by: 3,
        html_url: `https://github.com/nzbdav/nzbdav/compare/${buildSha}...dev`,
      }),
    );

    await expect(checkForUpdate("dev-e0eef52")).resolves.toEqual({
      kind: "dev",
      commitsBehind: 3,
      compareUrl: `https://github.com/nzbdav/nzbdav/compare/${buildSha}...dev`,
      trackRef: "dev",
    });
    expect(String(fetchMock.mock.calls[0]?.[0])).toContain(
      `/compare/${encodeURIComponent(buildSha)}...dev`,
    );
  });

  it("returns null when the running :dev image matches the dev tag tip", async () => {
    fetchMock.mockResolvedValueOnce(
      jsonResponse({
        status: "identical",
        ahead_by: 0,
        html_url: `https://github.com/nzbdav/nzbdav/compare/${buildSha}...dev`,
      }),
    );

    await expect(checkForUpdate("dev-e0eef52")).resolves.toBeNull();
    expect(String(fetchMock.mock.calls[0]?.[0])).toContain(
      `/compare/${encodeURIComponent(buildSha)}...dev`,
    );
  });

  it("returns null when compare status is identical", async () => {
    fetchMock.mockResolvedValueOnce(
      jsonResponse({
        status: "identical",
        ahead_by: 0,
        html_url: `https://github.com/nzbdav/nzbdav/compare/${buildSha}...dev`,
      }),
    );

    await expect(checkForUpdate("pre-42")).resolves.toBeNull();
  });

  it("returns null when compare status is behind", async () => {
    fetchMock.mockResolvedValueOnce(
      jsonResponse({
        status: "behind",
        ahead_by: 0,
        behind_by: 2,
        html_url: `https://github.com/nzbdav/nzbdav/compare/${buildSha}...dev`,
      }),
    );

    await expect(checkForUpdate("pre-42")).resolves.toBeNull();
  });

  it("returns null when compare status is diverged", async () => {
    fetchMock.mockResolvedValueOnce(
      jsonResponse({
        status: "diverged",
        ahead_by: 1,
        behind_by: 1,
        html_url: `https://github.com/nzbdav/nzbdav/compare/${buildSha}...dev`,
      }),
    );

    await expect(checkForUpdate("pre-42")).resolves.toBeNull();
  });

  it("returns null on 404 (local-only commit)", async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({ message: "Not Found" }, 404));
    await expect(checkForUpdate("pre-42")).resolves.toBeNull();
  });

  it("caches identical (no-update) results within the TTL", async () => {
    fetchMock.mockResolvedValueOnce(
      jsonResponse({
        status: "identical",
        ahead_by: 0,
        html_url: `https://github.com/nzbdav/nzbdav/compare/${buildSha}...dev`,
      }),
    );

    await expect(checkForUpdate("pre-42")).resolves.toBeNull();
    await expect(checkForUpdate("pre-42")).resolves.toBeNull();

    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("caches 404 (no-update) results within the TTL", async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({ message: "Not Found" }, 404));

    await expect(checkForUpdate("pre-42")).resolves.toBeNull();
    await expect(checkForUpdate("pre-42")).resolves.toBeNull();

    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("does not cache transient failures", async () => {
    fetchMock
      .mockRejectedValueOnce(new Error("network down"))
      .mockResolvedValueOnce(
        jsonResponse({
          status: "ahead",
          ahead_by: 2,
          html_url: `https://github.com/nzbdav/nzbdav/compare/${buildSha}...dev`,
        }),
      );

    await expect(checkForUpdate("pre-42")).resolves.toBeNull();
    await expect(checkForUpdate("pre-42")).resolves.toMatchObject({
      commitsBehind: 2,
      trackRef: "dev",
    });

    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it("does not cache rate-limited (403) responses", async () => {
    fetchMock
      .mockResolvedValueOnce(jsonResponse({ message: "rate limited" }, 403))
      .mockResolvedValueOnce(
        jsonResponse({
          status: "ahead",
          ahead_by: 1,
          html_url: `https://github.com/nzbdav/nzbdav/compare/${buildSha}...dev`,
        }),
      );

    await expect(checkForUpdate("pre-42")).resolves.toBeNull();
    await expect(checkForUpdate("pre-42")).resolves.toMatchObject({
      commitsBehind: 1,
      trackRef: "dev",
    });

    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it("refetches no-update results after the cache TTL expires", async () => {
    fetchMock
      .mockResolvedValueOnce(
        jsonResponse({
          status: "identical",
          ahead_by: 0,
          html_url: `https://github.com/nzbdav/nzbdav/compare/${buildSha}...dev`,
        }),
      )
      .mockResolvedValueOnce(
        jsonResponse({
          status: "ahead",
          ahead_by: 4,
          html_url: `https://github.com/nzbdav/nzbdav/compare/${buildSha}...dev`,
        }),
      );

    await expect(checkForUpdate("pre-42")).resolves.toBeNull();

    vi.advanceTimersByTime(60 * 60 * 1000);

    await expect(checkForUpdate("pre-42")).resolves.toMatchObject({
      commitsBehind: 4,
      trackRef: "dev",
    });
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it("falls back to commits page when html_url is missing", async () => {
    fetchMock.mockResolvedValueOnce(
      jsonResponse({
        status: "ahead",
        ahead_by: 1,
      }),
    );

    await expect(checkForUpdate("pre-42")).resolves.toEqual({
      kind: "dev",
      commitsBehind: 1,
      compareUrl: "https://github.com/nzbdav/nzbdav/commits/dev",
      trackRef: "dev",
    });
  });

  it("reuses the cached compare result within the TTL", async () => {
    fetchMock.mockResolvedValueOnce(
      jsonResponse({
        status: "ahead",
        ahead_by: 2,
        html_url: `https://github.com/nzbdav/nzbdav/compare/${buildSha}...dev`,
      }),
    );

    await checkForUpdate("pre-42");
    await checkForUpdate("pre-99");

    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("refetches compare after the cache TTL expires", async () => {
    fetchMock
      .mockResolvedValueOnce(
        jsonResponse({
          status: "ahead",
          ahead_by: 2,
          html_url: `https://github.com/nzbdav/nzbdav/compare/${buildSha}...dev`,
        }),
      )
      .mockResolvedValueOnce(
        jsonResponse({
          status: "ahead",
          ahead_by: 5,
          html_url: `https://github.com/nzbdav/nzbdav/compare/${buildSha}...dev`,
        }),
      );

    await expect(checkForUpdate("pre-42")).resolves.toMatchObject({
      commitsBehind: 2,
      trackRef: "dev",
    });

    vi.advanceTimersByTime(60 * 60 * 1000);

    await expect(checkForUpdate("pre-42")).resolves.toMatchObject({
      commitsBehind: 5,
      trackRef: "dev",
    });
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });
});
