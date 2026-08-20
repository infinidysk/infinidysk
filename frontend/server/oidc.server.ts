import * as oidc from "openid-client";
import type { UserRole } from "~/auth/authentication.server";
import { logger } from "./logger";

export type OidcClaims = Record<string, unknown>;

export type OidcSettings = {
  issuer: string;
  clientId: string;
  clientSecret: string;
  redirectUri?: string;
  scopes: string;
  usernameClaim: string;
  adminClaim?: string;
  adminClaimValue?: string;
};

const REQUIRED_ENV_VARS = ["OIDC_ISSUER", "OIDC_CLIENT_ID", "OIDC_CLIENT_SECRET"] as const;

let discoveryPromise: Promise<oidc.Configuration> | undefined;
let partialConfigWarningLogged = false;

function readEnv(name: string): string | undefined {
  const value = process.env[name]?.trim();
  return value ? value : undefined;
}

export function isOidcEnabled(): boolean {
  const configured = REQUIRED_ENV_VARS.filter((name) => readEnv(name) !== undefined);
  if (configured.length === 0) return false;

  if (configured.length !== REQUIRED_ENV_VARS.length) {
    if (!partialConfigWarningLogged) {
      const missing = REQUIRED_ENV_VARS.filter((name) => !readEnv(name));
      logger.warn(
        `OIDC is disabled because required environment variables are missing: ${missing.join(", ")}`,
      );
      partialConfigWarningLogged = true;
    }
    return false;
  }

  return true;
}

export function getOidcSettings(): OidcSettings {
  if (!isOidcEnabled()) {
    throw new Error("OIDC is not configured");
  }

  const redirectUri = readEnv("OIDC_REDIRECT_URI");
  const adminClaim = readEnv("OIDC_ADMIN_CLAIM");
  const adminClaimValue = readEnv("OIDC_ADMIN_CLAIM_VALUE");

  return {
    issuer: readEnv("OIDC_ISSUER")!,
    clientId: readEnv("OIDC_CLIENT_ID")!,
    clientSecret: readEnv("OIDC_CLIENT_SECRET")!,
    ...(redirectUri !== undefined ? { redirectUri } : {}),
    scopes: readEnv("OIDC_SCOPES") ?? "openid profile email",
    usernameClaim: readEnv("OIDC_USERNAME_CLAIM") ?? "preferred_username",
    ...(adminClaim !== undefined ? { adminClaim } : {}),
    ...(adminClaimValue !== undefined ? { adminClaimValue } : {}),
  };
}

export async function getOidcConfiguration(): Promise<oidc.Configuration> {
  const settings = getOidcSettings();
  discoveryPromise ??= oidc
    .discovery(new URL(settings.issuer), settings.clientId, settings.clientSecret, undefined, {
      execute: [oidc.enableNonRepudiationChecks],
    })
    .catch((error: unknown) => {
      discoveryPromise = undefined;
      throw error;
    });
  return discoveryPromise;
}

export function resolveOidcUsername(claims: OidcClaims): string {
  const configuredClaim = getOidcSettings().usernameClaim;
  const candidates = [configuredClaim, "preferred_username", "email", "sub"];

  for (const claimName of new Set(candidates)) {
    const value = claims[claimName];
    if (typeof value === "string" && value.trim()) {
      return value.trim();
    }
  }

  throw new Error("OIDC identity does not contain a usable username claim");
}

export function resolveOidcRole(claims: OidcClaims): UserRole {
  const { adminClaim, adminClaimValue } = getOidcSettings();
  if (!adminClaim) return "admin";
  if (!adminClaimValue) return "readonly";

  const value = claims[adminClaim];
  if (typeof value === "string") {
    return value === adminClaimValue ? "admin" : "readonly";
  }
  if (Array.isArray(value)) {
    return value.some((item) => item === adminClaimValue) ? "admin" : "readonly";
  }
  return "readonly";
}
