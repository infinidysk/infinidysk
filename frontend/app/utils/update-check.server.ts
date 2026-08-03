import { getBuildCommit, type BuildCommit } from "./version.server";
import {
  compareSemver,
  isComparableVersion,
  parseVersionParts,
  type DevUpdateAvailable,
  type ReleaseUpdateAvailable,
  type UpdateAvailable,
} from "./update-check";

export {
  compareSemver,
  isComparableVersion,
  type DevUpdateAvailable,
  type ReleaseUpdateAvailable,
  type UpdateAvailable,
};

const GITHUB_LATEST_RELEASE_URL =
  "https://api.github.com/repos/nzbdav/nzbdav/releases/latest";
const GITHUB_COMPARE_URL_PREFIX =
  "https://api.github.com/repos/nzbdav/nzbdav/compare/";
const CACHE_TTL_MS = 60 * 60 * 1000; // 1 hour
const FETCH_TIMEOUT_MS = 5_000;
const RELEASES_FALLBACK_URL = "https://github.com/nzbdav/nzbdav/releases";
const COMMITS_URL_PREFIX = "https://github.com/nzbdav/nzbdav/commits/";

type CachedRelease = {
  latestVersion: string;
  releaseUrl: string;
  checkedAt: number;
};

type CachedCompare = {
  commitsBehind: number;
  compareUrl: string;
  trackRef: string;
  checkedAt: number;
};

function compareFallbackUrl(trackRef: string): string {
  return `${COMMITS_URL_PREFIX}${encodeURIComponent(trackRef)}`;
}

let releaseCache: CachedRelease | null = null;
let releaseInFlight: Promise<CachedRelease | null> | null = null;

const compareCache = new Map<string, CachedCompare>();
const compareInFlight = new Map<string, Promise<CachedCompare | null>>();

/** Reset process-local cache (for tests). */
export function resetUpdateCheckCache(): void {
  releaseCache = null;
  releaseInFlight = null;
  compareCache.clear();
  compareInFlight.clear();
}

function isCacheFresh(checkedAt: number, now: number): boolean {
  return now - checkedAt < CACHE_TTL_MS;
}

async function fetchLatestRelease(): Promise<CachedRelease | null> {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), FETCH_TIMEOUT_MS);

  try {
    const response = await fetch(GITHUB_LATEST_RELEASE_URL, {
      signal: controller.signal,
      headers: {
        Accept: "application/vnd.github+json",
        "User-Agent": "nzbdav",
      },
    });

    if (!response.ok) return null;

    const data = (await response.json()) as {
      tag_name?: string;
      html_url?: string;
    };

    const tag = data.tag_name?.trim();
    if (!tag) return null;

    const latestVersion = tag.replace(/^v/i, "");
    if (!parseVersionParts(latestVersion)) return null;

    return {
      latestVersion,
      releaseUrl: data.html_url?.trim() || RELEASES_FALLBACK_URL,
      checkedAt: Date.now(),
    };
  } catch {
    return null;
  } finally {
    clearTimeout(timeout);
  }
}

async function getCachedLatestRelease(): Promise<CachedRelease | null> {
  const now = Date.now();
  if (releaseCache && isCacheFresh(releaseCache.checkedAt, now)) {
    return releaseCache;
  }

  if (!releaseInFlight) {
    releaseInFlight = fetchLatestRelease()
      .then((result) => {
        if (result) {
          releaseCache = result;
        }
        return result;
      })
      .finally(() => {
        releaseInFlight = null;
      });
  }

  return releaseInFlight;
}

/**
 * Fetches the compare result for `sha...branch`. Returns a cacheable entry for
 * every definitive answer — including "no update" (commitsBehind 0) for
 * identical/behind/diverged statuses and 404 (commit unknown to GitHub) — so
 * up-to-date builds don't re-query on every page load. Returns null only for
 * transient failures (network errors, rate limiting, 5xx), which stay uncached.
 */
async function fetchCompare(
  sha: string,
  trackRef: string,
): Promise<CachedCompare | null> {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), FETCH_TIMEOUT_MS);
  const fallbackUrl = compareFallbackUrl(trackRef);

  const noUpdate: CachedCompare = {
    commitsBehind: 0,
    compareUrl: fallbackUrl,
    trackRef,
    checkedAt: Date.now(),
  };

  try {
    const url = `${GITHUB_COMPARE_URL_PREFIX}${encodeURIComponent(sha)}...${encodeURIComponent(trackRef)}`;
    const response = await fetch(url, {
      signal: controller.signal,
      headers: {
        Accept: "application/vnd.github+json",
        "User-Agent": "nzbdav",
      },
    });

    // 404 means the commit is unknown to GitHub (local-only) — a definitive
    // "no update" that won't change until the build commit changes.
    if (response.status === 404) return noUpdate;
    if (!response.ok) return null;

    const data = (await response.json()) as {
      status?: string;
      ahead_by?: number;
      html_url?: string;
    };

    // We only notify when the track ref is ahead of the running commit.
    if (data.status !== "ahead") return noUpdate;

    const aheadBy = data.ahead_by ?? 0;
    if (aheadBy <= 0) return noUpdate;

    return {
      commitsBehind: aheadBy,
      compareUrl: data.html_url?.trim() || fallbackUrl,
      trackRef,
      checkedAt: Date.now(),
    };
  } catch {
    return null;
  } finally {
    clearTimeout(timeout);
  }
}

async function getCachedCompare(build: BuildCommit): Promise<CachedCompare | null> {
  const cacheKey = `${build.sha}...${build.branch}`;
  const now = Date.now();
  const cached = compareCache.get(cacheKey);
  if (cached && isCacheFresh(cached.checkedAt, now)) {
    return cached;
  }

  const existing = compareInFlight.get(cacheKey);
  if (existing) return existing;

  const promise = fetchCompare(build.sha, build.branch)
    .then((result) => {
      if (result) {
        compareCache.set(cacheKey, result);
      }
      return result;
    })
    .finally(() => {
      compareInFlight.delete(cacheKey);
    });

  compareInFlight.set(cacheKey, promise);
  return promise;
}

async function checkForReleaseUpdate(
  currentVersion: string,
): Promise<ReleaseUpdateAvailable | null> {
  const latest = await getCachedLatestRelease();
  if (!latest) return null;

  if (compareSemver(latest.latestVersion, currentVersion) <= 0) {
    return null;
  }

  return {
    kind: "release",
    latestVersion: latest.latestVersion,
    releaseUrl: latest.releaseUrl,
  };
}

async function checkForDevUpdate(
  localGitOnly = false,
  currentVersion?: string | null,
): Promise<DevUpdateAvailable | null> {
  const build = await getBuildCommit({ version: currentVersion });
  if (!build) return null;
  if (localGitOnly && build.source !== "git") return null;

  const compare = await getCachedCompare(build);
  if (!compare || compare.commitsBehind <= 0) return null;

  return {
    kind: "dev",
    commitsBehind: compare.commitsBehind,
    compareUrl: compare.compareUrl,
    trackRef: build.branch,
  };
}

/**
 * Returns update metadata when a newer stable GitHub release exists than
 * `currentVersion`, or when a non-release build is behind commits on its
 * track ref (`main` for source/`main-<sha>` builds, `dev` for `:dev` images).
 * Failures yield null.
 */
export async function checkForUpdate(
  currentVersion: string | undefined | null,
): Promise<UpdateAvailable | null> {
  if (isComparableVersion(currentVersion)) {
    const releaseUpdate = await checkForReleaseUpdate(currentVersion);
    if (releaseUpdate) return releaseUpdate;

    // A source checkout still reads the latest released version from
    // version.txt. If its local main commit is stale, compare it with GitHub
    // after confirming there is no newer release. Release Docker images use
    // the env-provided SHA and intentionally remain release-only.
    return checkForDevUpdate(true);
  }

  // Non-comparable versions (e.g. `main-<sha>`) may embed the build commit in
  // the version label itself, so pass it along as a fallback SHA source.
  return checkForDevUpdate(false, currentVersion);
}
