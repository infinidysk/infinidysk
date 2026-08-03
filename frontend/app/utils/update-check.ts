export type ReleaseUpdateAvailable = {
  kind: "release";
  latestVersion: string;
  releaseUrl: string;
};

export type DevUpdateAvailable = {
  kind: "dev";
  commitsBehind: number;
  compareUrl: string;
  /** GitHub ref compared against (e.g. `main` or `dev`). */
  trackRef: string;
};

export type UpdateAvailable = ReleaseUpdateAvailable | DevUpdateAvailable;

export function isComparableVersion(version: string | undefined | null): version is string {
  if (!version) return false;
  const trimmed = version.trim();
  if (!trimmed || trimmed.toLowerCase() === "unknown") return false;
  if (trimmed === "0.0.0") return false;
  if (/^pre-/i.test(trimmed)) return false;
  return parseVersionParts(trimmed) !== null;
}

/** Compare dotted semver strings. Returns positive if a > b, negative if a < b, 0 if equal. */
export function compareSemver(a: string, b: string): number {
  const partsA = parseVersionParts(a);
  const partsB = parseVersionParts(b);
  if (!partsA || !partsB) return 0;

  const len = Math.max(partsA.length, partsB.length);
  for (let i = 0; i < len; i++) {
    const left = partsA[i] ?? 0;
    const right = partsB[i] ?? 0;
    if (left !== right) return left - right;
  }
  return 0;
}

export function parseVersionParts(version: string): number[] | null {
  const normalized = version.trim().replace(/^v/i, "");
  const match = /^(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:\.(\d+))?$/.exec(normalized);
  if (!match) return null;
  return match.slice(1).filter((p) => p !== undefined).map((p) => Number(p));
}
