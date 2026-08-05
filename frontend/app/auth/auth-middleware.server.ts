import type express from "express";
import { isAuthenticated } from "~/auth/authentication.server";
import { normalizeUrlBase } from "~/utils/url-base";
import { safeDecodePath } from "../../server/proxy-path";

// The Express server mounts middleware under URL_BASE, so `req.path` here is
// already stripped of the prefix and PUBLIC_PATHS work unchanged. But
// `res.redirect("/login")` emits an absolute path the browser resolves against
// the origin, not the mount point — outgoing Location values need the prefix
// put back on. Read at runtime, same as the mount in server.ts.
const URL_BASE = normalizeUrlBase(process.env.URL_BASE);

// Paths that do not require authentication. Every other path is protected.
const PUBLIC_PATHS = [
  "/__manifest",
  "/login",
  "/login.data",
  "/onboarding",
  "/onboarding.data",
  "/auth/oidc/login",
  "/auth/oidc/callback",
];

export async function authMiddleware(
  req: express.Request,
  res: express.Response,
  next: express.NextFunction,
): Promise<void> {
  // Allow explicitly public paths (malformed encoding is not public)
  const pathname = safeDecodePath(req.path);
  if (pathname !== null && PUBLIC_PATHS.includes(pathname)) return next();

  // Allow authenticated sessions
  if (await isAuthenticated(req)) return next();

  // Redirect everything else to the login page
  res.redirect(302, `${URL_BASE}/login`);
}
