import { Router, type Request, type Response } from "express";
import * as oidc from "openid-client";
import {
  clearOidcFlowState,
  getOidcFlowState,
  setOidcFlowState,
  setSessionUser,
  type SessionResponseInit,
} from "~/auth/authentication.server";
import {
  getOidcConfiguration,
  getOidcSettings,
  isOidcEnabled,
  resolveOidcRole,
  resolveOidcUsername,
} from "./oidc.server";
import { logger } from "./logger";
import { backendClient } from "~/clients/backend-client.server";

export const oidcRouter = Router();

oidcRouter.get("/auth/oidc/login", oidcLoginHandler);
oidcRouter.get("/auth/oidc/callback", oidcCallbackHandler);

export async function oidcLoginHandler(req: Request, res: Response): Promise<void> {
  if (!isOidcEnabled()) {
    res.redirect(302, "/login?error=oidc_not_configured");
    return;
  }

  try {
    const configuration = await getOidcConfiguration();
    const settings = getOidcSettings();
    const codeVerifier = oidc.randomPKCECodeVerifier();
    const codeChallenge = await oidc.calculatePKCECodeChallenge(codeVerifier);
    const nonce = oidc.randomNonce();
    const state = oidc.randomState();
    const redirectUri = await resolveRedirectUri(req);
    const session = await setOidcFlowState(req, {
      codeVerifier,
      nonce,
      redirectUri,
      state,
    });

    applySetCookie(res, session);
    const authorizationUrl = oidc.buildAuthorizationUrl(configuration, {
      redirect_uri: redirectUri,
      scope: settings.scopes,
      code_challenge: codeChallenge,
      code_challenge_method: "S256",
      nonce,
      state,
    });
    res.redirect(302, authorizationUrl.href);
  } catch (error) {
    logOidcFailure("Could not start OIDC sign-in", error);
    res.redirect(302, "/login?error=oidc_failed");
  }
}

export async function oidcCallbackHandler(req: Request, res: Response): Promise<void> {
  if (!isOidcEnabled()) {
    res.redirect(302, "/login?error=oidc_not_configured");
    return;
  }

  try {
    const flow = await getOidcFlowState(req);
    if (!flow) {
      throw new Error("OIDC sign-in state is missing or expired");
    }

    const configuration = await getOidcConfiguration();
    const tokens = await oidc.authorizationCodeGrant(
      configuration,
      buildCallbackUrl(req, flow.redirectUri),
      {
        pkceCodeVerifier: flow.codeVerifier,
        expectedNonce: flow.nonce,
        expectedState: flow.state,
      },
    );
    const claims = tokens.claims();
    if (!claims) {
      throw new Error("OIDC provider did not return ID token claims");
    }

    const username = resolveOidcUsername(claims);
    const role = resolveOidcRole(claims);
    const session = await setSessionUser(req, username, role);

    applySetCookie(res, session);
    logger.info(`OIDC sign-in succeeded for ${username} with ${role} role`);
    res.redirect(302, "/");
  } catch (error) {
    logOidcFailure("OIDC sign-in failed", error);
    const session = await clearOidcFlowState(req);
    applySetCookie(res, session);
    res.redirect(302, "/login?error=oidc_failed");
  }
}

async function resolveRedirectUri(req: Request): Promise<string> {
  const configured = getOidcSettings().redirectUri;
  if (configured) return new URL(configured).href;

  try {
    const config = await backendClient.getConfig(["general.base-url"]);
    const baseUrl = config.find((item) => item.configName === "general.base-url")
      ?.configValue
      ?.trim();
    if (baseUrl) {
      return new URL(
        "auth/oidc/callback",
        baseUrl.endsWith("/") ? baseUrl : `${baseUrl}/`,
      ).href;
    }
  } catch (error) {
    logger.debug("Could not read general.base-url for OIDC callback", error);
  }

  const host = req.get("host");
  if (!host) throw new Error("Unable to determine OIDC callback host");
  return new URL("/auth/oidc/callback", `${req.protocol}://${host}`).href;
}

function buildCallbackUrl(req: Request, redirectUri: string): URL {
  const callbackUrl = new URL(redirectUri);
  const queryIndex = req.originalUrl.indexOf("?");
  callbackUrl.search = queryIndex === -1 ? "" : req.originalUrl.slice(queryIndex);
  return callbackUrl;
}

function applySetCookie(res: Response, responseInit: SessionResponseInit): void {
  const setCookie = new Headers(responseInit.headers).get("Set-Cookie");
  if (setCookie) res.setHeader("Set-Cookie", setCookie);
}

function logOidcFailure(message: string, error: unknown): void {
  const reason = error instanceof Error ? error.message : String(error);
  logger.warn(`${message}. Reason: ${reason}`);
  if (error instanceof Error) logger.debug(`${message} stack`, error);
}
