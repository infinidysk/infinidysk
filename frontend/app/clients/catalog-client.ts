import type { z } from "zod";
import { withUrlBase } from "~/utils/url-base";
import {
  catalogPlaybackSchema,
  catalogSearchResponseSchema,
  catalogTitleResponseSchema,
  type CatalogHistoryProgressRequest,
  type CatalogPlayback,
  type CatalogPlaybackRequest,
  type CatalogSearchPage,
  type CatalogTitleResponse,
} from "./catalog-contract";

/**
 * Parses a catalog response, preferring the backend's sanitized error message.
 * Contract mismatches surface as the fallback so a provider change never renders
 * an undefined field into the UI.
 */
async function readCatalogResponse<T>(
  response: Response,
  schema: z.ZodType<T>,
  fallback: string,
): Promise<T> {
  let payload: unknown;
  try {
    payload = await response.json();
  } catch {
    payload = null;
  }

  if (!response.ok) {
    throw new Error(reportedMessage(payload) ?? fallback);
  }

  const parsed = schema.safeParse(payload);
  if (!parsed.success) {
    throw new Error(fallback);
  }
  return parsed.data;
}

/**
 * Admin failures arrive as problem details (`detail`/`title`); the legacy
 * `{ status, error }` envelope is still accepted for older route shapes.
 */
function reportedMessage(payload: unknown): string | null {
  if (!payload || typeof payload !== "object") return null;
  const record = payload as Record<string, unknown>;
  for (const key of ["detail", "title", "error"]) {
    const value = record[key];
    if (typeof value === "string" && value.length > 0) return value;
  }
  return null;
}

/** Browser-side search paging for the catalog's "Load more" control. */
export async function fetchCatalogSearch(
  query: string,
  page: number,
  signal: AbortSignal,
): Promise<CatalogSearchPage> {
  const parameters = new URLSearchParams({ q: query, page: String(page) });
  const response = await fetch(withUrlBase(`/api/catalog/search?${parameters}`), {
    signal,
    headers: { Accept: "application/json" },
  });
  return readCatalogResponse(response, catalogSearchResponseSchema, "Could not load more titles.");
}

/** Starts a playback session for one movie or episode. */
export async function createCatalogPlayback(
  request: CatalogPlaybackRequest,
  signal: AbortSignal,
): Promise<CatalogPlayback> {
  const response = await fetch(withUrlBase("/api/catalog/playback"), {
    method: "POST",
    signal,
    headers: { "Content-Type": "application/json", Accept: "application/json" },
    body: JSON.stringify(request),
  });
  return readCatalogResponse(response, catalogPlaybackSchema, "Could not start playback.");
}

/**
 * Records playback position for Continue Watching. Fire-and-forget: a failed report only
 * costs resume accuracy, so callers must not surface it as a playback error.
 */
export async function reportWatchHistoryProgress(
  request: CatalogHistoryProgressRequest,
  signal?: AbortSignal,
): Promise<void> {
  await fetch(withUrlBase("/api/catalog/history/progress"), {
    method: "POST",
    ...(signal ? { signal } : {}),
    headers: { "Content-Type": "application/json", Accept: "application/json" },
    body: JSON.stringify(request),
    keepalive: true,
  });
}

/** Drops one row from Continue Watching (and from history) by id. */
export async function removeCatalogHistoryItem(id: number, signal?: AbortSignal): Promise<void> {
  const response = await fetch(withUrlBase(`/api/catalog/history/${id}`), {
    method: "DELETE",
    ...(signal ? { signal } : {}),
    headers: { Accept: "application/json" },
  });
  if (!response.ok) throw new Error("Could not remove this title from Continue Watching.");
}

/** One season's episodes, used to roll a season finale into the next season. */
export async function fetchCatalogSeason(
  tmdbId: number,
  season: number,
  signal: AbortSignal,
): Promise<CatalogTitleResponse> {
  const parameters = new URLSearchParams({ season: String(season) });
  const response = await fetch(withUrlBase(`/api/catalog/titles/series/${tmdbId}?${parameters}`), {
    signal,
    headers: { Accept: "application/json" },
  });
  return readCatalogResponse(
    response,
    catalogTitleResponseSchema,
    "Could not load the next season.",
  );
}
