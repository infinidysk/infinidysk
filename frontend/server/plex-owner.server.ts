import { createHmac } from "node:crypto";

export const PLEX_OWNER_HEADER = "x-infinidysk-plex-owner";

/** Only call after verifying an admin session. Never pass a browser-provided identity. */
export function buildPlexOwnerHeaders(verifiedSessionIdentity: string, sharedKey: string, now = Date.now()): Record<typeof PLEX_OWNER_HEADER, string> {
  if (!verifiedSessionIdentity || !sharedKey) throw new Error("A verified Plex owner session is required.");
  const owner = createHmac("sha256", sharedKey).update(`plex-owner\n${verifiedSessionIdentity}`).digest("hex");
  const expiry = Math.floor(now / 1000) + 120;
  const signature = createHmac("sha256", sharedKey).update(`plex-owner-v1\n${owner}\n${expiry}`).digest("hex");
  return { [PLEX_OWNER_HEADER]: `${owner}.${expiry}.${signature}` };
}

/** Proxy boundary: remove untrusted claims before optionally replacing them. */
export function applyPlexOwnerHeaders(headers: Record<string, string | string[] | undefined>, verifiedSessionIdentity: string | null, sharedKey: string, now = Date.now()): void {
  for (const name of Object.keys(headers)) {
    if (name.toLowerCase() === PLEX_OWNER_HEADER) delete headers[name];
  }
  if (verifiedSessionIdentity) Object.assign(headers, buildPlexOwnerHeaders(verifiedSessionIdentity, sharedKey, now));
}
