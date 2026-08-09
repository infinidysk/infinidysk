import http from "node:http";
import { generateKeyPairSync, sign, type KeyObject } from "node:crypto";
import type { AddressInfo } from "node:net";
import type { Request, Response } from "express";
import * as oidc from "openid-client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const mocks = vi.hoisted(() => ({
  getOidcFlowState: vi.fn(),
  setSessionUser: vi.fn(),
  clearOidcFlowState: vi.fn(),
  getOidcConfiguration: vi.fn(),
}));

vi.mock("~/auth/authentication.server", () => ({
  getOidcFlowState: mocks.getOidcFlowState,
  setSessionUser: mocks.setSessionUser,
  clearOidcFlowState: mocks.clearOidcFlowState,
}));

vi.mock("~/clients/backend-client.server", () => ({
  backendClient: {
    getConfig: vi.fn(),
  },
}));

vi.mock("./oidc.server", async (importOriginal) => {
  const actual = await importOriginal<typeof import("./oidc.server")>();
  return {
    ...actual,
    getOidcConfiguration: mocks.getOidcConfiguration,
    isOidcEnabled: () => true,
  };
});

vi.mock("./logger", () => ({
  logger: {
    info: vi.fn(),
    warn: vi.fn(),
    debug: vi.fn(),
  },
}));

import { oidcCallbackHandler } from "./oidc-routes";

const CLIENT_ID = "nzbdav";
const CLIENT_SECRET = "secret";
const REDIRECT_URI = "https://nzbdav.example.com/auth/oidc/callback";
const FLOW = {
  codeVerifier: "fixture-code-verifier",
  nonce: "fixture-nonce",
  redirectUri: REDIRECT_URI,
  state: "fixture-state",
};
const OIDC_ENV_NAMES = [
  "OIDC_ISSUER",
  "OIDC_CLIENT_ID",
  "OIDC_CLIENT_SECRET",
  "OIDC_USERNAME_CLAIM",
  "OIDC_ADMIN_CLAIM",
  "OIDC_ADMIN_CLAIM_VALUE",
] as const;

type OidcFixture = {
  configuration: oidc.Configuration;
  server: http.Server;
  tokenRequest: () => URLSearchParams | undefined;
};

const originalOidcEnv = Object.fromEntries(
  OIDC_ENV_NAMES.map((name) => [name, process.env[name]]),
);
const servers: http.Server[] = [];

beforeEach(() => {
  vi.clearAllMocks();
  process.env["OIDC_ISSUER"] = "https://fixture.invalid";
  process.env["OIDC_CLIENT_ID"] = CLIENT_ID;
  process.env["OIDC_CLIENT_SECRET"] = CLIENT_SECRET;
  process.env["OIDC_USERNAME_CLAIM"] = "preferred_username";
  process.env["OIDC_ADMIN_CLAIM"] = "groups";
  process.env["OIDC_ADMIN_CLAIM_VALUE"] = "nzbdav-admins";
  mocks.getOidcFlowState.mockResolvedValue(FLOW);
  mocks.setSessionUser.mockResolvedValue({
    headers: { "Set-Cookie": "__session=user" },
  });
  mocks.clearOidcFlowState.mockResolvedValue({
    headers: { "Set-Cookie": "__session=cleared" },
  });
});

afterEach(async () => {
  for (const name of OIDC_ENV_NAMES) {
    const value = originalOidcEnv[name];
    if (value === undefined) delete process.env[name];
    else process.env[name] = value;
  }
  await Promise.all(servers.splice(0).map(closeServer));
});

describe("OIDC callback integration", () => {
  it("discovers the provider, validates its ID token, and creates a session", async () => {
    const fixture = await startOidcFixture();
    mocks.getOidcConfiguration.mockResolvedValue(fixture.configuration);
    const req = mockRequest(
      "/auth/oidc/callback?code=fixture-code&state=fixture-state",
    );
    const res = mockResponse();

    await oidcCallbackHandler(req, res);

    expect(fixture.tokenRequest()).toEqual(expect.any(URLSearchParams));
    expect(fixture.tokenRequest()?.get("client_id")).toBe(CLIENT_ID);
    expect(fixture.tokenRequest()?.get("client_secret")).toBe(CLIENT_SECRET);
    expect(fixture.tokenRequest()?.get("code")).toBe("fixture-code");
    expect(fixture.tokenRequest()?.get("code_verifier")).toBe(FLOW.codeVerifier);
    expect(fixture.tokenRequest()?.get("redirect_uri")).toBe(REDIRECT_URI);
    expect(mocks.setSessionUser).toHaveBeenCalledWith(req, "alice", "admin");
    expect(res.setHeader).toHaveBeenCalledWith("Set-Cookie", "__session=user");
    expect(res.redirect).toHaveBeenCalledWith(302, "/");
  });

  it("rejects an ID token not signed by the discovered provider key", async () => {
    const fixture = await startOidcFixture(false);
    mocks.getOidcConfiguration.mockResolvedValue(fixture.configuration);
    const req = mockRequest(
      "/auth/oidc/callback?code=fixture-code&state=fixture-state",
    );
    const res = mockResponse();

    await oidcCallbackHandler(req, res);

    expect(mocks.setSessionUser).not.toHaveBeenCalled();
    expect(mocks.clearOidcFlowState).toHaveBeenCalledWith(req);
    expect(res.setHeader).toHaveBeenCalledWith("Set-Cookie", "__session=cleared");
    expect(res.redirect).toHaveBeenCalledWith(302, "/login?error=oidc_failed");
  });
});

async function startOidcFixture(trustedSignature = true): Promise<OidcFixture> {
  const trustedKeys = generateKeyPairSync("rsa", { modulusLength: 2048 });
  const untrustedKeys = generateKeyPairSync("rsa", { modulusLength: 2048 });
  const signingKey = trustedSignature ? trustedKeys.privateKey : untrustedKeys.privateKey;
  const publicJwk = {
    ...trustedKeys.publicKey.export({ format: "jwk" }),
    alg: "RS256",
    kid: "fixture-key",
    use: "sig",
  };
  let issuer = "";
  let lastTokenRequest: URLSearchParams | undefined;

  const server = http.createServer((req, res) => {
    if (req.url === "/.well-known/openid-configuration") {
      sendJson(res, {
        issuer,
        authorization_endpoint: `${issuer}/authorize`,
        token_endpoint: `${issuer}/token`,
        jwks_uri: `${issuer}/jwks`,
        response_types_supported: ["code"],
        subject_types_supported: ["public"],
        id_token_signing_alg_values_supported: ["RS256"],
        token_endpoint_auth_methods_supported: ["client_secret_post"],
        code_challenge_methods_supported: ["S256"],
      });
      return;
    }

    if (req.url === "/jwks") {
      sendJson(res, { keys: [publicJwk] });
      return;
    }

    if (req.url === "/token" && req.method === "POST") {
      let body = "";
      req.setEncoding("utf8");
      req.on("data", (chunk: string) => {
        body += chunk;
      });
      req.on("end", () => {
        lastTokenRequest = new URLSearchParams(body);
        const now = Math.floor(Date.now() / 1000);
        sendJson(res, {
          access_token: "fixture-access-token",
          token_type: "Bearer",
          expires_in: 300,
          id_token: signJwt(
            {
              iss: issuer,
              sub: "fixture-user",
              aud: CLIENT_ID,
              iat: now,
              exp: now + 300,
              nonce: FLOW.nonce,
              preferred_username: "alice",
              groups: ["nzbdav-admins"],
            },
            signingKey,
          ),
        });
      });
      return;
    }

    res.writeHead(404);
    res.end();
  });
  servers.push(server);

  const port = await listen(server);
  issuer = `http://127.0.0.1:${port}`;
  const configuration = await oidc.discovery(
    new URL(issuer),
    CLIENT_ID,
    CLIENT_SECRET,
    undefined,
    {
      execute: [
        oidc.allowInsecureRequests,
        oidc.enableNonRepudiationChecks,
      ],
    },
  );

  return {
    configuration,
    server,
    tokenRequest: () => lastTokenRequest,
  };
}

function signJwt(claims: Record<string, unknown>, privateKey: KeyObject): string {
  const header = encodeJson({ alg: "RS256", kid: "fixture-key", typ: "JWT" });
  const payload = encodeJson(claims);
  const signingInput = `${header}.${payload}`;
  const signature = sign("RSA-SHA256", Buffer.from(signingInput), privateKey);
  return `${signingInput}.${signature.toString("base64url")}`;
}

function encodeJson(value: Record<string, unknown>): string {
  return Buffer.from(JSON.stringify(value)).toString("base64url");
}

function sendJson(res: http.ServerResponse, value: unknown): void {
  res.writeHead(200, {
    "Content-Type": "application/json",
    "Cache-Control": "no-store",
  });
  res.end(JSON.stringify(value));
}

function listen(server: http.Server): Promise<number> {
  return new Promise((resolve, reject) => {
    server.listen(0, "127.0.0.1", () => {
      const address = server.address() as AddressInfo | null;
      if (!address) {
        reject(new Error("OIDC fixture has no address"));
        return;
      }
      resolve(address.port);
    });
    server.on("error", reject);
  });
}

function closeServer(server: http.Server): Promise<void> {
  return new Promise((resolve, reject) => {
    server.close((error) => {
      if (error) reject(error);
      else resolve();
    });
  });
}

function mockRequest(originalUrl: string): Request {
  return {
    protocol: "https",
    originalUrl,
    get: vi.fn((name: string) => name === "host" ? "nzbdav.example.com" : undefined),
  } as unknown as Request;
}

function mockResponse(): Response & {
  redirect: ReturnType<typeof vi.fn>;
  setHeader: ReturnType<typeof vi.fn>;
} {
  return {
    redirect: vi.fn(),
    setHeader: vi.fn(),
  } as unknown as Response & {
    redirect: ReturnType<typeof vi.fn>;
    setHeader: ReturnType<typeof vi.fn>;
  };
}
