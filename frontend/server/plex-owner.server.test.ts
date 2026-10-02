import { describe, expect, it } from "vitest";
import { createHmac } from "node:crypto";
import { applyPlexOwnerHeaders, buildPlexOwnerHeaders } from "./plex-owner.server";

describe("Plex owner attestation", () => {
  it("uses a session-specific opaque owner and short signed expiry", () => {
    const first = buildPlexOwnerHeaders("session-one", "key", 1_800_000_000_000);
    const proof = first["x-infinidysk-plex-owner"];
    const [owner, expiry, signature] = proof.split(".");
    expect(proof).not.toContain("session-one");
    expect(expiry).toBe("1800000120");
    expect(signature).toBe(createHmac("sha256", "key").update(`plex-owner-v1\n${owner}\n${expiry}`).digest("hex"));
    expect(buildPlexOwnerHeaders("session-two", "key", 1_800_000_000_000)).not.toEqual(first);
  });

  it("strips browser claims unless an authenticated admin identity was verified", () => {
    const headers: Record<string, string | string[] | undefined> = { "x-infinidysk-plex-owner": "forged" };
    applyPlexOwnerHeaders(headers, null, "key");
    expect(headers["x-infinidysk-plex-owner"]).toBeUndefined();
    headers["x-infinidysk-plex-owner"] = "forged";
    applyPlexOwnerHeaders(headers, "verified-nonce", "key", 1_800_000_000_000);
    expect(headers["x-infinidysk-plex-owner"]).toEqual(buildPlexOwnerHeaders("verified-nonce", "key", 1_800_000_000_000)["x-infinidysk-plex-owner"]);
  });
});
