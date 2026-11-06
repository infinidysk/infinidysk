import { readFile } from "node:fs/promises";
import { resolve } from "node:path";

const versionFilePath = resolve(process.cwd(), "..", "version.txt");
const gitDirPath = resolve(process.cwd(), "..", ".git");

/** 1.x lives on the `v1` branch; `main` is 2.x. */
const TRACK_BRANCH = "v1";
const LOCAL_TRACKED_REFS = new Set(["refs/heads/v1", "refs/heads/main"]);

export type BuildCommit = {
  sha: string;
  branch: string;
  source: "env" | "git" | "version";
};

type BuildCommitOptions = {
  gitDir?: string;
  /** Version label that may embed a commit SHA (e.g. `main-e0eef520`). */
  version?: string | null;
};

export async function getAppVersion(): Promise<string | undefined> {
  if (process.env["NZBDAV_VERSION"]) {
    return process.env["NZBDAV_VERSION"];
  }

  try {
    return (await readFile(versionFilePath, "utf8")).trim() || undefined;
  } catch {
    return undefined;
  }
}

/**
 * Resolve the commit SHA of the running build.
 *
 * Preference order:
 * 1. `NZBDAV_COMMIT_SHA` env (baked into Docker images)
 * 2. Local `.git` checkout when HEAD is on `v1` (or a 1.x `main` from before
 *    the branch split)
 * 3. Commit SHA embedded in the version label (`v1-<sha>` / `main-<sha>`)
 *
 * Other branches and detached HEAD return undefined so feature-branch /
 * fork checkouts do not produce false update notifications. Every 1.x build
 * compares against `v1`.
 *
 * Env builds use a track ref derived from the version label: `dev-*` images
 * compare against the movable `dev` tag (which tracks `:dev`), not `v1`.
 */
export async function getBuildCommit(
  options: BuildCommitOptions = {},
): Promise<BuildCommit | undefined> {
  const { gitDir = gitDirPath, version } = options;

  const envSha = process.env["NZBDAV_COMMIT_SHA"]?.trim();
  if (envSha && isValidSha(envSha)) {
    return {
      sha: envSha.toLowerCase(),
      branch: resolveTrackRef(version),
      source: "env",
    };
  }

  const local = await readLocalTrackedCommit(gitDir);
  if (local) return local;

  return parseBuildCommitFromVersion(version);
}

/**
 * GitHub compare target for a build. Docker `:dev` images (`dev-<sha>`) track
 * the movable `dev` tag; everything else compares against `v1`.
 */
export function resolveTrackRef(version?: string | null): string {
  const label = (version ?? process.env["NZBDAV_VERSION"])?.trim() ?? "";
  if (/^dev-/i.test(label)) return "dev";
  return TRACK_BRANCH;
}

/** Parse a commit SHA embedded in a `v1-<sha>` or `main-<sha>` version label. */
export function parseBuildCommitFromVersion(
  version: string | undefined | null,
): BuildCommit | undefined {
  if (!version) return undefined;
  const match = /^(?:v1|main)-([0-9a-f]{7,40})$/i.exec(version.trim());
  if (!match) return undefined;
  const sha = match[1];
  if (!sha) return undefined;
  return { sha: sha.toLowerCase(), branch: TRACK_BRANCH, source: "version" };
}

async function readLocalTrackedCommit(gitDir: string): Promise<BuildCommit | undefined> {
  try {
    const head = (await readFile(resolve(gitDir, "HEAD"), "utf8")).trim();
    const refMatch = /^ref:\s*(.+)$/i.exec(head);
    if (!refMatch) {
      // Detached HEAD — skip.
      return undefined;
    }

    const ref = refMatch[1]?.trim();
    if (!ref || !LOCAL_TRACKED_REFS.has(ref)) {
      return undefined;
    }

    const sha = await resolveGitRef(gitDir, ref);
    if (!sha) return undefined;

    return { sha, branch: TRACK_BRANCH, source: "git" };
  } catch {
    return undefined;
  }
}

async function resolveGitRef(gitDir: string, ref: string): Promise<string | undefined> {
  try {
    const sha = (await readFile(resolve(gitDir, ref), "utf8")).trim();
    if (isValidSha(sha)) return sha.toLowerCase();
  } catch {
    // Fall through to packed-refs.
  }

  try {
    const packed = await readFile(resolve(gitDir, "packed-refs"), "utf8");
    for (const line of packed.split("\n")) {
      const trimmed = line.trim();
      if (!trimmed || trimmed.startsWith("#") || trimmed.startsWith("^")) continue;
      const [sha, packedRef] = trimmed.split(/\s+/);
      if (packedRef === ref && sha !== undefined && isValidSha(sha)) {
        return sha.toLowerCase();
      }
    }
  } catch {
    return undefined;
  }

  return undefined;
}

function isValidSha(value: string): boolean {
  return /^[0-9a-f]{7,40}$/i.test(value.trim());
}
