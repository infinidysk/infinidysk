import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("./logger", () => ({
  logger: {
    warn: vi.fn(),
  },
}));

import {
  getOidcSettings,
  isOidcEnabled,
  resolveOidcRole,
  resolveOidcUsername,
} from "./oidc.server";

const requiredEnvironment = {
  OIDC_ISSUER: "https://identity.example.com",
  OIDC_CLIENT_ID: "nzbdav",
  OIDC_CLIENT_SECRET: "secret",
};

beforeEach(() => {
  for (const name of [
    "OIDC_ISSUER",
    "OIDC_CLIENT_ID",
    "OIDC_CLIENT_SECRET",
    "OIDC_REDIRECT_URI",
    "OIDC_SCOPES",
    "OIDC_USERNAME_CLAIM",
    "OIDC_ADMIN_CLAIM",
    "OIDC_ADMIN_CLAIM_VALUE",
  ]) {
    vi.stubEnv(name, "");
  }
});

afterEach(() => {
  vi.unstubAllEnvs();
});

function enableOidc(): void {
  for (const [name, value] of Object.entries(requiredEnvironment)) {
    vi.stubEnv(name, value);
  }
}

describe("OIDC configuration", () => {
  it("is disabled unless all required variables are configured", () => {
    expect(isOidcEnabled()).toBe(false);

    vi.stubEnv("OIDC_ISSUER", requiredEnvironment.OIDC_ISSUER);
    expect(isOidcEnabled()).toBe(false);

    enableOidc();
    expect(isOidcEnabled()).toBe(true);
  });

  it("uses secure defaults for optional settings", () => {
    enableOidc();

    expect(getOidcSettings()).toEqual({
      issuer: requiredEnvironment.OIDC_ISSUER,
      clientId: requiredEnvironment.OIDC_CLIENT_ID,
      clientSecret: requiredEnvironment.OIDC_CLIENT_SECRET,
      redirectUri: undefined,
      scopes: "openid profile email",
      usernameClaim: "preferred_username",
      adminClaim: undefined,
      adminClaimValue: undefined,
    });
  });

  it("resolves the configured username claim with standard fallbacks", () => {
    enableOidc();
    vi.stubEnv("OIDC_USERNAME_CLAIM", "nickname");

    expect(resolveOidcUsername({ nickname: "alice", email: "alice@example.com" }))
      .toBe("alice");
    expect(resolveOidcUsername({ email: "alice@example.com" }))
      .toBe("alice@example.com");
    expect(() => resolveOidcUsername({ name: "Alice" }))
      .toThrow("does not contain a usable username claim");
  });

  it("maps matching string and array claims to admin", () => {
    enableOidc();
    vi.stubEnv("OIDC_ADMIN_CLAIM", "groups");
    vi.stubEnv("OIDC_ADMIN_CLAIM_VALUE", "nzbdav-admins");

    expect(resolveOidcRole({ groups: "nzbdav-admins" })).toBe("admin");
    expect(resolveOidcRole({ groups: ["users", "nzbdav-admins"] })).toBe("admin");
    expect(resolveOidcRole({ groups: ["users"] })).toBe("readonly");
  });

  it("defaults to admin only when role mapping is not configured", () => {
    enableOidc();
    expect(resolveOidcRole({})).toBe("admin");

    vi.stubEnv("OIDC_ADMIN_CLAIM", "groups");
    expect(resolveOidcRole({ groups: ["nzbdav-admins"] })).toBe("readonly");
  });
});
