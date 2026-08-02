import type express from "express";
import { isAuthenticated } from "~/auth/authentication.server";
import { safeDecodePath } from "../../server/proxy-path";

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
  res.redirect(302, "/login");
}
