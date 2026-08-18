import type express from "express";
import { isAuthenticated } from "~/auth/authentication.server";
import { isBackendApiDocsPath, isBackendApiPath, isBackendMetricsPath } from "./proxy-path";

/**
 * Inject the frontend→backend API key for authenticated UI sessions that proxy
 * `/api` or `/metrics` without supplying their own key.
 */
export async function setApiKeyForAuthenticatedRequests(
  req: express.Request,
): Promise<void> {
  // if the path is not a protected backend endpoint, do nothing
  if (!isBackendApiPath(req.path)
    && !isBackendMetricsPath(req.path)
    && !isBackendApiDocsPath(req.path)) return;

  const apikey = req.query["apikey"] || req.query["apiKey"] || req.headers["x-api-key"];
  const hasApiKey = apikey && typeof apikey === "string";

  // if the request already has an apikey, do nothing
  if (hasApiKey) return;

  // if the request is not authenticated, do nothing
  const authenticated = await isAuthenticated(req);
  if (!authenticated) return;

  // otherwise, set the api key header
  req.headers["x-api-key"] = process.env["FRONTEND_BACKEND_API_KEY"] || "";
}
