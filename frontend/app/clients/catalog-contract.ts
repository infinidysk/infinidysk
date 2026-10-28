import { z } from "zod";

const nullableText = z.string().nullable();
const nullableNumber = z.number().nullable();

export const catalogTitleTypeSchema = z.enum(["movie", "series"]);
export type CatalogTitleType = z.infer<typeof catalogTitleTypeSchema>;

/** Poster-card summary shared by every catalog surface. */
export const catalogSummarySchema = z.strictObject({
  type: catalogTitleTypeSchema,
  tmdbId: z.number().int().positive(),
  title: z.string().min(1),
  year: z.number().int().nullable(),
  overview: nullableText,
  rating: nullableNumber,
  posterUrl: nullableText,
  backdropUrl: nullableText,
});
export type CatalogSummary = z.infer<typeof catalogSummarySchema>;

export const catalogRowSchema = z.strictObject({
  id: z.string().min(1),
  title: z.string().min(1),
  items: z.array(catalogSummarySchema),
});
export type CatalogRow = z.infer<typeof catalogRowSchema>;

export const catalogHomeSchema = z.strictObject({
  status: z.literal(true),
  error: nullableText.optional(),
  rows: z.array(catalogRowSchema),
});
export type CatalogHome = z.infer<typeof catalogHomeSchema>;

export const catalogSearchResponseSchema = z.strictObject({
  status: z.literal(true),
  error: nullableText.optional(),
  page: z.number().int(),
  totalPages: z.number().int(),
  hasMore: z.boolean(),
  results: z.array(catalogSummarySchema),
});
export type CatalogSearchPage = z.infer<typeof catalogSearchResponseSchema>;

export const catalogSeasonSchema = z.strictObject({
  seasonNumber: z.number().int(),
  name: z.string(),
  episodeCount: z.number().int(),
});
export type CatalogSeason = z.infer<typeof catalogSeasonSchema>;

export const catalogEpisodeSchema = z.strictObject({
  seasonNumber: z.number().int(),
  episodeNumber: z.number().int(),
  name: z.string(),
  overview: nullableText,
  airDate: nullableText,
  runtimeMinutes: z.number().int().nullable(),
  stillUrl: nullableText,
});
export type CatalogEpisode = z.infer<typeof catalogEpisodeSchema>;

export const catalogTitleSchema = z.strictObject({
  type: catalogTitleTypeSchema,
  tmdbId: z.number().int().positive(),
  title: z.string().min(1),
  year: z.number().int().nullable(),
  overview: nullableText,
  rating: nullableNumber,
  runtimeMinutes: z.number().int().nullable(),
  genres: z.array(z.string()),
  posterUrl: nullableText,
  backdropUrl: nullableText,
});
export type CatalogTitle = z.infer<typeof catalogTitleSchema>;

export const catalogTitleResponseSchema = z.strictObject({
  status: z.literal(true),
  error: nullableText.optional(),
  title: catalogTitleSchema.nullable(),
  seasons: z.array(catalogSeasonSchema),
  episodes: z.array(catalogEpisodeSchema),
  selectedSeason: z.number().int().nullable(),
});
export type CatalogTitleResponse = z.infer<typeof catalogTitleResponseSchema>;

export const catalogPlaybackSchema = z.strictObject({
  status: z.literal(true),
  error: nullableText.optional(),
  playbackId: z.string().min(1),
  directUrl: z.string().min(1),
  hlsUrl: z.string().min(1),
  releaseTitle: z.string(),
});
export type CatalogPlayback = z.infer<typeof catalogPlaybackSchema>;

/**
 * Playback coordinates. Only provider ids and episode coordinates travel to the backend —
 * external ids, profile tokens, and NZB URLs are resolved server-side.
 */
export type CatalogPlaybackRequest = {
  type: CatalogTitleType;
  tmdbId: number;
  season?: number;
  episode?: number;
};
