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

/** Trending-people card. Identity is the TMDB person id, mirroring the title cards. */
export const catalogPersonSchema = z.strictObject({
  personId: z.string().min(1),
  name: z.string().min(1),
  profileUrl: nullableText,
});
export type CatalogPerson = z.infer<typeof catalogPersonSchema>;

/**
 * Home row. Exactly one of `items` and `people` is a list; the other is null, because
 * the trending-people row is not a title list. A row with neither is a contract break.
 */
export const catalogRowSchema = z
  .strictObject({
    id: z.string().min(1),
    title: z.string().min(1),
    items: z.array(catalogSummarySchema).nullable(),
    people: z.array(catalogPersonSchema).nullable(),
  })
  .refine((row) => (row.items === null) !== (row.people === null), {
    message: "A catalog row carries either items or people.",
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

export const catalogPersonDetailsSchema = z.strictObject({
  personId: z.string().min(1),
  name: z.string().min(1),
  profileUrl: nullableText,
  biography: nullableText,
  birthday: nullableText,
  placeOfBirth: nullableText,
  knownForDepartment: nullableText,
});
export type CatalogPersonDetails = z.infer<typeof catalogPersonDetailsSchema>;

export const catalogPersonResponseSchema = z.strictObject({
  status: z.literal(true),
  error: nullableText.optional(),
  person: catalogPersonDetailsSchema.nullable(),
  credits: z.array(catalogSummarySchema),
});
export type CatalogPersonResponse = z.infer<typeof catalogPersonResponseSchema>;

/** One Continue Watching row: last position plus everything needed to resume it. */
export const catalogHistoryItemSchema = z.strictObject({
  id: z.number().int().positive(),
  type: catalogTitleTypeSchema,
  tmdbId: z.number().int().positive(),
  season: z.number().int().nullable(),
  episode: z.number().int().nullable(),
  title: z.string(),
  subtitle: nullableText,
  posterUrl: nullableText,
  positionSeconds: z.number().int().nonnegative(),
  durationSeconds: z.number().int().nonnegative(),
  updatedAtEpoch: z.number().int(),
  finished: z.boolean(),
});
export type CatalogHistoryItem = z.infer<typeof catalogHistoryItemSchema>;

export const catalogHistoryResponseSchema = z.strictObject({
  status: z.literal(true),
  error: nullableText.optional(),
  items: z.array(catalogHistoryItemSchema),
});
export type CatalogHistoryResponse = z.infer<typeof catalogHistoryResponseSchema>;

/**
 * Progress report. Display text never travels from the browser: the backend resolves
 * titles and artwork from provider metadata for the reported coordinates.
 */
export type CatalogHistoryProgressRequest = {
  type: CatalogTitleType;
  tmdbId: number;
  season?: number;
  episode?: number;
  position: number;
  duration: number | null;
  finished: boolean;
};
