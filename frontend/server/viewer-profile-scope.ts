import type express from "express";

/** Header the frontend attaches so backend watch state is scoped to the picked profile. */
export const VIEWER_PROFILE_HEADER = "x-viewer-profile-id";

/** A profile session may only proxy catalog and catalog media-token endpoints. */
export function isViewerProfileAllowedBackendPath(pathname: string): boolean {
  return pathname.startsWith("/api/catalog/") || pathname.startsWith("/api/search/");
}

/** Strip any client-supplied value, then set the session's profile id when present. */
export function applyViewerProfileScope(req: express.Request, profileId: number | null): void {
  delete req.headers[VIEWER_PROFILE_HEADER];
  if (profileId !== null) req.headers[VIEWER_PROFILE_HEADER] = String(profileId);
}
