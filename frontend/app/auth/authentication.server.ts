import { createCookieSessionStorage } from "react-router";
import crypto from "crypto";
import fs from "fs";
import path from "path";
import { backendClient } from "~/clients/backend-client.server";
import type { IncomingMessage } from "http";

export const IS_FRONTEND_AUTH_DISABLED = process.env["DISABLE_FRONTEND_AUTH"] === 'true';

export type UserRole = "admin" | "readonly";

export type User = {
  username: string;
  role: UserRole;
};

export type OidcFlowState = {
  codeVerifier: string;
  nonce: string;
  redirectUri: string;
  state: string;
};

export type SessionResponseInit = {
  headers: {
    "Set-Cookie": string;
  };
};

const oneYear = 60 * 60 * 24 * 365; // seconds

function resolveSessionMaxAgeSeconds(): number {
  const raw = process.env["SESSION_MAX_AGE"]?.trim();
  if (!raw) return oneYear;
  const parsed = Number.parseInt(raw, 10);
  if (!Number.isFinite(parsed) || parsed <= 0) return oneYear;
  return parsed;
}

function resolveSessionKey(): string {
  if (process.env["SESSION_KEY"]) return process.env["SESSION_KEY"];

  const configPath = process.env["CONFIG_PATH"];
  if (configPath) {
    const keyPath = path.join(configPath, "session.key");
    try {
      if (fs.existsSync(keyPath)) {
        const existing = fs.readFileSync(keyPath, "utf8").trim();
        if (existing.length > 0) {
          process.env["SESSION_KEY"] = existing;
          return existing;
        }
      }
      const generated = crypto.randomBytes(64).toString("hex");
      fs.mkdirSync(configPath, { recursive: true });
      fs.writeFileSync(keyPath, generated, { encoding: "utf8", mode: 0o600 });
      process.env["SESSION_KEY"] = generated;
      return generated;
    } catch {
      // Fall through to ephemeral key if CONFIG_PATH is unwritable.
    }
  }

  const ephemeral = crypto.randomBytes(64).toString("hex");
  process.env["SESSION_KEY"] = ephemeral;
  return ephemeral;
}

const sessionKey = resolveSessionKey();
const sessionMaxAge = resolveSessionMaxAgeSeconds();
const oidcConfigured = ["OIDC_ISSUER", "OIDC_CLIENT_ID", "OIDC_CLIENT_SECRET"]
  .every((name) => Boolean(process.env[name]?.trim()));
const secureCookiesExplicit = process.env["SECURE_COOKIES"] !== undefined && process.env["SECURE_COOKIES"] !== "";
if (!secureCookiesExplicit && !IS_FRONTEND_AUTH_DISABLED) {
  console.warn(
    "SECURE_COOKIES is unset; session cookies will be sent over HTTP. Set SECURE_COOKIES=true behind HTTPS.",
  );
}

const sessionStorage = createCookieSessionStorage({
  cookie: {
    name: "__session",
    httpOnly: true,
    path: "/",
    // OIDC callbacks are top-level cross-site navigations, which require Lax.
    sameSite: oidcConfigured ? "lax" : "strict",
    secrets: [sessionKey],
    secure: ["true", "yes"].includes(process?.env?.["SECURE_COOKIES"] || ""),
    maxAge: sessionMaxAge,
  },
});

export async function isAuthenticated(request: Request | IncomingMessage): Promise<boolean> {
  // If auth is disabled, always return true
  if (IS_FRONTEND_AUTH_DISABLED) return true;

  // Otherwise, check session storage
  return (await getSessionUser(request)) !== null;
}

export async function getSessionUser(request: Request | IncomingMessage): Promise<User | null> {
  if (IS_FRONTEND_AUTH_DISABLED) return null;

  const cookieHeader = getCookieHeader(request);
  if (!cookieHeader) return null;

  const session = await sessionStorage.getSession(cookieHeader);
  const user = session.get("user") as User | undefined;
  if (!user?.username) return null;
  return {
    username: user.username,
    role: user.role === "readonly" ? "readonly" : "admin",
  };
}

export async function login(request: Request): Promise<SessionResponseInit> {
  const user = await authenticate(request);
  const session = await sessionStorage.getSession(request.headers.get("cookie"));
  session.set("user", user);
  return { headers: { "Set-Cookie": await sessionStorage.commitSession(session) } };
}

export async function logout(request: Request | IncomingMessage): Promise<SessionResponseInit> {
  const session = await sessionStorage.getSession(getCookieHeader(request));
  session.unset("user");
  return { headers: { "Set-Cookie": await sessionStorage.commitSession(session) } };
}

export async function setSessionUser(
  request: Request | IncomingMessage,
  username: string,
  role: UserRole = "admin",
): Promise<SessionResponseInit> {
  const session = await sessionStorage.getSession(getCookieHeader(request));
  session.set("user", { username, role });
  session.unset("oidcFlow");
  return { headers: { "Set-Cookie": await sessionStorage.commitSession(session) } };
}

export async function setOidcFlowState(
  request: Request | IncomingMessage,
  oidcFlow: OidcFlowState,
): Promise<SessionResponseInit> {
  const session = await sessionStorage.getSession(getCookieHeader(request));
  session.set("oidcFlow", oidcFlow);
  return { headers: { "Set-Cookie": await sessionStorage.commitSession(session) } };
}

export async function getOidcFlowState(
  request: Request | IncomingMessage,
): Promise<OidcFlowState | null> {
  const session = await sessionStorage.getSession(getCookieHeader(request));
  const oidcFlow = session.get("oidcFlow") as OidcFlowState | undefined;
  if (
    !oidcFlow?.codeVerifier
    || !oidcFlow.nonce
    || !oidcFlow.redirectUri
    || !oidcFlow.state
  ) return null;
  return oidcFlow;
}

export async function clearOidcFlowState(
  request: Request | IncomingMessage,
): Promise<SessionResponseInit> {
  const session = await sessionStorage.getSession(getCookieHeader(request));
  session.unset("oidcFlow");
  return { headers: { "Set-Cookie": await sessionStorage.commitSession(session) } };
}

async function authenticate(request: Request): Promise<User> {
  const formData = await request.formData();
  const username = formData.get("username");
  const password = formData.get("password");
  if (typeof username !== "string" || typeof password !== "string" || !username || !password) {
    throw new Error("username and password required");
  }
  if (await backendClient.authenticate(username, password)) {
    return { username, role: "admin" };
  }
  throw new Error("Invalid credentials");
}

function getCookieHeader(request: Request | IncomingMessage): string | null | undefined {
  return request instanceof Request
    ? request.headers.get("cookie")
    : request.headers.cookie;
}
