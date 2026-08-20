import { beforeEach, describe, expect, it, vi } from "vitest";
import type { Request, Response } from "express";

const mocks = vi.hoisted(() => ({
  randomPKCECodeVerifier: vi.fn(() => "verifier"),
  calculatePKCECodeChallenge: vi.fn(() => Promise.resolve("challenge")),
  randomNonce: vi.fn(() => "nonce"),
  randomState: vi.fn(() => "state"),
  buildAuthorizationUrl: vi.fn(() => new URL("https://identity.example.com/authorize")),
  authorizationCodeGrant: vi.fn(),
  setOidcFlowState: vi.fn(),
  getOidcFlowState: vi.fn(),
  setSessionUser: vi.fn(),
  clearOidcFlowState: vi.fn(),
  getOidcConfiguration: vi.fn(),
  getOidcSettings: vi.fn(),
  isOidcEnabled: vi.fn(),
  resolveOidcRole: vi.fn(),
  resolveOidcUsername: vi.fn(),
  getConfig: vi.fn(),
}));

vi.mock("openid-client", () => ({
  randomPKCECodeVerifier: mocks.randomPKCECodeVerifier,
  calculatePKCECodeChallenge: mocks.calculatePKCECodeChallenge,
  randomNonce: mocks.randomNonce,
  randomState: mocks.randomState,
  buildAuthorizationUrl: mocks.buildAuthorizationUrl,
  authorizationCodeGrant: mocks.authorizationCodeGrant,
}));

vi.mock("~/auth/authentication.server", () => ({
  setOidcFlowState: mocks.setOidcFlowState,
  getOidcFlowState: mocks.getOidcFlowState,
  setSessionUser: mocks.setSessionUser,
  clearOidcFlowState: mocks.clearOidcFlowState,
}));

vi.mock("~/clients/backend-client.server", () => ({
  backendClient: {
    getConfig: mocks.getConfig,
  },
}));

vi.mock("./oidc.server", () => ({
  getOidcConfiguration: mocks.getOidcConfiguration,
  getOidcSettings: mocks.getOidcSettings,
  isOidcEnabled: mocks.isOidcEnabled,
  resolveOidcRole: mocks.resolveOidcRole,
  resolveOidcUsername: mocks.resolveOidcUsername,
}));

vi.mock("./logger", () => ({
  logger: {
    info: vi.fn(),
    warn: vi.fn(),
    debug: vi.fn(),
  },
}));

import { oidcCallbackHandler, oidcLoginHandler } from "./oidc-routes";

function mockRequest(originalUrl: string): Request {
  return {
    protocol: "https",
    originalUrl,
    get: vi.fn((name: string) => (name === "host" ? "nzbdav.example.com" : undefined)),
  } as unknown as Request;
}

// Omit<> keeps the mock members lint-clean (pure vi.fn, no `this`); callers cast
// back to express.Response at the handler boundary (the mock only implements the
// surface the handlers exercise).
type MockResponse = Omit<Response, "redirect" | "setHeader"> & {
  redirect: ReturnType<typeof vi.fn>;
  setHeader: ReturnType<typeof vi.fn>;
};

function mockResponse(): MockResponse {
  return {
    redirect: vi.fn(),
    setHeader: vi.fn(),
  } as unknown as MockResponse;
}

beforeEach(() => {
  vi.clearAllMocks();
  mocks.isOidcEnabled.mockReturnValue(true);
  mocks.getOidcConfiguration.mockResolvedValue({});
  mocks.getOidcSettings.mockReturnValue({
    issuer: "https://identity.example.com",
    clientId: "nzbdav",
    clientSecret: "secret",
    scopes: "openid profile email",
    usernameClaim: "preferred_username",
  });
  mocks.getConfig.mockResolvedValue([]);
  mocks.setOidcFlowState.mockResolvedValue({
    headers: { "Set-Cookie": "__session=flow" },
  });
  mocks.clearOidcFlowState.mockResolvedValue({
    headers: { "Set-Cookie": "__session=cleared" },
  });
});

describe("OIDC Express routes", () => {
  it("starts an authorization-code flow with PKCE and state", async () => {
    const req = mockRequest("/auth/oidc/login");
    const res = mockResponse();

    await oidcLoginHandler(req, res as unknown as Response);

    expect(mocks.setOidcFlowState).toHaveBeenCalledWith(req, {
      codeVerifier: "verifier",
      nonce: "nonce",
      redirectUri: "https://nzbdav.example.com/auth/oidc/callback",
      state: "state",
    });
    expect(mocks.buildAuthorizationUrl).toHaveBeenCalledWith(
      {},
      expect.objectContaining({
        redirect_uri: "https://nzbdav.example.com/auth/oidc/callback",
        code_challenge: "challenge",
        code_challenge_method: "S256",
        nonce: "nonce",
        state: "state",
      }),
    );
    expect(res.setHeader).toHaveBeenCalledWith("Set-Cookie", "__session=flow");
    expect(res.redirect).toHaveBeenCalledWith(302, "https://identity.example.com/authorize");
  });

  it("exchanges the callback and creates a role-aware session", async () => {
    const req = mockRequest("/auth/oidc/callback?code=code&state=state");
    const res = mockResponse();
    mocks.getOidcFlowState.mockResolvedValue({
      codeVerifier: "verifier",
      nonce: "nonce",
      redirectUri: "https://nzbdav.example.com/auth/oidc/callback",
      state: "state",
    });
    mocks.authorizationCodeGrant.mockResolvedValue({
      claims: () => ({ preferred_username: "alice", groups: ["admins"] }),
    });
    mocks.resolveOidcUsername.mockReturnValue("alice");
    mocks.resolveOidcRole.mockReturnValue("admin");
    mocks.setSessionUser.mockResolvedValue({
      headers: { "Set-Cookie": "__session=user" },
    });

    await oidcCallbackHandler(req, res as unknown as Response);

    expect(mocks.authorizationCodeGrant).toHaveBeenCalledWith(
      {},
      new URL("https://nzbdav.example.com/auth/oidc/callback?code=code&state=state"),
      {
        pkceCodeVerifier: "verifier",
        expectedNonce: "nonce",
        expectedState: "state",
      },
    );
    expect(mocks.setSessionUser).toHaveBeenCalledWith(req, "alice", "admin");
    expect(res.setHeader).toHaveBeenCalledWith("Set-Cookie", "__session=user");
    expect(res.redirect).toHaveBeenCalledWith(302, "/");
  });

  it("clears failed callback state and returns to login", async () => {
    const req = mockRequest("/auth/oidc/callback?error=access_denied");
    const res = mockResponse();
    mocks.getOidcFlowState.mockResolvedValue(null);

    await oidcCallbackHandler(req, res as unknown as Response);

    expect(mocks.clearOidcFlowState).toHaveBeenCalledWith(req);
    expect(res.setHeader).toHaveBeenCalledWith("Set-Cookie", "__session=cleared");
    expect(res.redirect).toHaveBeenCalledWith(302, "/login?error=oidc_failed");
  });
});
