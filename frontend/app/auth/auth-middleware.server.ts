import type express from "express";
import { getSessionUser, isAuthenticated } from "~/auth/authentication.server";
import { URL_BASE } from "~/utils/url-base";
import { safeDecodePath } from "../../server/proxy-path";

// The Express server mounts middleware under URL_BASE, so `req.path` here is
// already stripped of the prefix and PUBLIC_PATHS work unchanged. But
// `res.redirect("/login")` emits an absolute path the browser resolves against
// the origin, not the mount point — outgoing Location values need the prefix
// put back on. This module is part of the Vite SSR bundle, so the baked
// URL_BASE constant applies (server.ts guarantees it matches the runtime env).

// Paths that do not require authentication. Every other path is protected.
const PUBLIC_PATHS = [
  "/__manifest",
  "/login",
  "/login.data",
  "/login/admin",
  "/login/admin.data",
  "/onboarding",
  "/onboarding.data",
  "/auth/oidc/login",
  "/auth/oidc/callback",
];

// A profile session may only reach the catalog, its own logout, and public paths.
const VIEWER_PROFILE_ALLOWED_PREFIXES = ["/watch", "/logout"];
function isViewerProfileAllowedPath(pathname: string | null): boolean {
  if (pathname === null) return false;
  return VIEWER_PROFILE_ALLOWED_PREFIXES.some(
    (prefix) =>
      pathname === prefix || pathname.startsWith(`${prefix}/`) || pathname.startsWith(`${prefix}.`),
  );
}

export async function authMiddleware(
  req: express.Request,
  res: express.Response,
  next: express.NextFunction,
): Promise<void> {
  // Allow explicitly public paths (malformed encoding is not public)
  const pathname = safeDecodePath(req.path);
  if (pathname !== null && PUBLIC_PATHS.includes(pathname)) return next();

  // A profile session may only reach the catalog, its own logout, and public paths
  const user = await getSessionUser(req);
  if (user?.role === "profile" && !isViewerProfileAllowedPath(pathname)) {
    res.redirect(302, `${URL_BASE}/watch`);
    return;
  }

  // Allow authenticated sessions
  if (await isAuthenticated(req)) return next();

  // Redirect everything else to the login page
  res.redirect(302, `${URL_BASE}/login`);
}
