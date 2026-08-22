import { describe, expect, it } from "vitest";
import { buildProfileAdapterUrl } from "./profile-adapter-urls";

describe("buildProfileAdapterUrl", () => {
  const origin = "https://stream.example";
  const token = "profile-token";
  const urlBase = "/infinidysk";

  it.each([
    [
      "json",
      "https://stream.example/infinidysk/api/search/profile-token/lookup?type=movie&id=tt0111161",
    ],
    ["newznab", "https://stream.example/infinidysk/adapters/newznab/profile-token/api"],
    ["addon", "https://stream.example/infinidysk/adapters/addon/profile-token/manifest.json"],
  ] as const)("includes URL_BASE for the %s adapter", (adapter, expected) => {
    expect(buildProfileAdapterUrl(origin, adapter, token, urlBase)).toBe(expected);
  });

  it("keeps root-mounted adapter URLs unchanged", () => {
    expect(buildProfileAdapterUrl(origin, "addon", token, "")).toBe(
      "https://stream.example/adapters/addon/profile-token/manifest.json",
    );
  });
});
