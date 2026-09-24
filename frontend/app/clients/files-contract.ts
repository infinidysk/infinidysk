import { z } from "zod";

const instant = z.iso.datetime({ offset: true });
const nullableText = z.string().nullable();
const nullableId = z.guid().nullable();
const count = z.number().int().nonnegative();
export const fileHealthSchema = z.enum(["healthy", "degraded", "needs-attention", "unknown"]);
export type FileHealth = z.infer<typeof fileHealthSchema>;
export const fileRowSchema = z.strictObject({
  key: z.string().min(1),
  id: nullableId,
  parentId: nullableId,
  name: z.string(),
  path: z.string(),
  isDirectory: z.boolean(),
  hasChildren: z.boolean(),
  size: count.nullable(),
  subType: z.number().int().nullable(),
  addedAt: instant.nullable(),
  releaseDate: instant.nullable(),
  lastHealthCheck: instant.nullable(),
  nextCheckAt: instant.nullable(),
  health: fileHealthSchema.nullable(),
  scanState: z
    .enum([
      "not-applicable",
      "disabled",
      "checking",
      "repair-pending",
      "queued",
      "due",
      "scheduled",
    ])
    .nullable(),
  progress: count.max(100).nullable(),
  healthResultId: nullableId,
  healthResultAt: instant.nullable(),
  repairAction: z.number().int().min(0).max(4).nullable(),
  healthMessage: nullableText,
  historyItemId: nullableId,
  jobName: nullableText,
  nzbFileName: nullableText,
  category: nullableText,
  indexerName: nullableText,
  lastPlayedAt: instant.nullable(),
  nzbBlobId: nullableId,
  libraryState: z.enum(["in-library", "not-in-library", "unknown", "not-configured"]).nullable(),
  libraryLinkCount: count,
  libraryPaths: z.array(z.string()),
  canRecheck: z.boolean(),
  recheckDisabledReason: nullableText,
  canDelete: z.boolean(),
  deleteDisabledReason: nullableText,
  canSearchArr: z.boolean(),
  searchArrDisabledReason: nullableText,
});
export type FileRow = z.infer<typeof fileRowSchema>;
const pageShape = {
  status: z.literal(true),
  error: nullableText.optional(),
  mode: z.enum(["tree", "list"]),
  scopePath: z.string(),
  parentPath: z.string(),
  offset: count,
  limit: count.min(1).max(200),
  totalRows: count,
  matchingFileCount: count,
  hasMore: z.boolean(),
  observedAt: instant,
  libraryScanState: z.enum(["ready", "unknown", "not-configured"]),
  libraryScannedAt: instant.nullable(),
  libraryError: nullableText,
  schedule: z.object({
    timeZoneId: z.string(),
    checksOpen: z.boolean(),
    repairsOpen: z.boolean(),
    nextChecksChange: instant.nullable(),
    nextRepairsChange: instant.nullable(),
    pendingRepairCount: count,
    manualRunActive: z.boolean(),
  }),
};
function uniqueKeys(page: { rows: { key: string }[] }) {
  return new Set(page.rows.map((row) => row.key)).size === page.rows.length;
}
export const browseFilesResponseSchema = z
  .strictObject({ ...pageShape, rows: z.array(fileRowSchema).max(200) })
  .refine(uniqueKeys, "Duplicate row keys.");
export type BrowseFilesResponse = z.infer<typeof browseFilesResponseSchema>;
export const fileResourceRowSchema = fileRowSchema.extend({
  mimeType: z.string(),
  previewUrl: nullableText,
});
export type FileResourceRow = z.infer<typeof fileResourceRowSchema>;
export const filesResourcePageSchema = z
  .strictObject({ ...pageShape, rows: z.array(fileResourceRowSchema).max(200) })
  .refine(uniqueKeys, "Duplicate row keys.");
export type FilesResourcePage = z.infer<typeof filesResourcePageSchema>;
export const recheckFileResponseSchema = z.object({
  status: z.literal(true),
  davItemId: z.guid(),
  state: z.enum(["queued", "already-queued", "already-running"]),
});
export type RecheckFileResponse = z.infer<typeof recheckFileResponseSchema>;
export const searchFileInArrResponseSchema = z.object({
  status: z.literal(true),
  davItemId: z.guid(),
  outcome: z.enum(["requested", "partial", "unconfirmed", "failed"]),
  results: z
    .array(
      z.object({
        appType: z.enum(["radarr", "sonarr"]),
        instanceName: z.string(),
        mediaIds: z.array(count.min(1)).min(1),
        commandId: count.min(1).nullable(),
        state: z.enum(["requested", "failed", "unconfirmed", "not-requested"]),
        error: nullableText,
      }),
    )
    .min(1)
    .max(32),
});
export type SearchFileInArrResponse = z.infer<typeof searchFileInArrResponseSchema>;
export const deletePreviewResponseSchema = z.object({
  status: z.literal(true),
  fileCount: count,
  dirCount: count,
  totalBytes: count,
  linkedHistoryCount: count,
});
export type DeletePreviewResponse = z.infer<typeof deletePreviewResponseSchema>;
const epoch = z.number().int().min(-62135596800).max(253402300799).nullable();
export const filesFiltersSchema = z
  .object({
    q: z.string().trim().max(256).default(""),
    health: z.array(fileHealthSchema).default([]),
    schedule: z
      .enum([
        "all",
        "due",
        "scheduled",
        "recheck-queued",
        "repair-pending",
        "never-scheduled",
        "checking",
      ])
      .default("all"),
    library: z
      .enum(["all", "in-library", "not-in-library", "unknown", "not-configured"])
      .default("all"),
    category: z.string().max(255).default(""),
    indexer: z.string().max(255).default(""),
    subType: z
      .union([z.literal(201), z.literal(202), z.literal(203)])
      .nullable()
      .default(null),
    repairAction: z.number().int().min(0).max(4).nullable().default(null),
    hasNzb: z.boolean().nullable().default(null),
    minSize: count.max(Number.MAX_SAFE_INTEGER).nullable().default(null),
    maxSize: count.max(Number.MAX_SAFE_INTEGER).nullable().default(null),
    addedAfter: epoch.default(null),
    addedBefore: epoch.default(null),
    postedAfter: epoch.default(null),
    postedBefore: epoch.default(null),
    checkedAfter: epoch.default(null),
    checkedBefore: epoch.default(null),
    playedAfter: epoch.default(null),
    playedBefore: epoch.default(null),
  })
  .superRefine((filters, context) => {
    for (const [lower, upper] of [
      ["minSize", "maxSize"],
      ["addedAfter", "addedBefore"],
      ["postedAfter", "postedBefore"],
      ["checkedAfter", "checkedBefore"],
      ["playedAfter", "playedBefore"],
    ] as const) {
      if (filters[lower] !== null && filters[upper] !== null && filters[lower] > filters[upper])
        context.addIssue({
          code: "custom",
          path: [upper],
          message: "Must not be smaller than the lower bound.",
        });
    }
  });
export type FilesFilters = z.infer<typeof filesFiltersSchema>;
export const defaultFilesFilters: FilesFilters = filesFiltersSchema.parse({});
const numericFilters = new Set([
  "subType",
  "repairAction",
  "minSize",
  "maxSize",
  "addedAfter",
  "addedBefore",
  "postedAfter",
  "postedBefore",
  "checkedAfter",
  "checkedBefore",
  "playedAfter",
  "playedBefore",
]);
export function normalizeContentPath(value: string): string {
  const path = value.endsWith("/") ? value.slice(0, -1) : value;
  if (
    path.includes("\0") ||
    (path !== "/content" && !path.startsWith("/content/")) ||
    path
      .split("/")
      .slice(1)
      .some((part) => ["", ".", ".."].includes(part))
  )
    throw new Error("Invalid content path.");
  return path;
}
function single(params: URLSearchParams, name: string): string | null {
  if (params.getAll(name).length > 1) throw new Error(`Expected one ${name} value.`);
  return params.get(name) || null;
}
function integer(value: string, name: string): number {
  if (!/^[+-]?\d+$/.test(value.trim()) || !Number.isSafeInteger(Number(value)))
    throw new Error(`Invalid ${name}.`);
  return Number(value);
}
export function parseFilesFilters(params: URLSearchParams): FilesFilters {
  const values: Record<string, unknown> = {};
  for (const name of Object.keys(defaultFilesFilters)) {
    const value = single(params, name);
    if (value === null) continue;
    values[name] =
      name === "health"
        ? [
            ...new Set(
              value
                .split(",")
                .map((part) => part.trim())
                .filter(Boolean),
            ),
          ]
        : name === "hasNzb"
          ? value === "true"
            ? true
            : value === "false"
              ? false
              : value
          : numericFilters.has(name)
            ? integer(value, name)
            : value;
  }
  return filesFiltersSchema.parse(values);
}
export function serializeFilesFilters(filters: FilesFilters): URLSearchParams {
  const validated = filesFiltersSchema.parse(filters);
  const params = new URLSearchParams();
  for (const [name, value] of Object.entries(validated)) {
    if (name === "health") {
      if (validated.health.length)
        params.set(name, [...new Set(validated.health)].sort().join(","));
    } else if (value !== null && value !== "" && value !== "all") params.set(name, String(value));
  }
  return params;
}
export function parseFilesParameters(params: URLSearchParams): URLSearchParams {
  const result = serializeFilesFilters(parseFilesFilters(params));
  const scope = normalizeContentPath(single(params, "scopePath") ?? "/content");
  const parent = normalizeContentPath(single(params, "parentPath") ?? scope);
  const mode = z.enum(["tree", "list"]).parse(single(params, "mode") ?? "tree");
  if (mode === "tree" && parent !== scope && !parent.startsWith(scope + "/"))
    throw new Error("Parent must be inside scope.");
  result.set("scopePath", scope);
  result.set("parentPath", parent);
  result.set("mode", mode);
  result.set(
    "offset",
    String(count.max(2147483647).parse(integer(single(params, "offset") ?? "0", "offset"))),
  );
  result.set(
    "limit",
    String(
      count
        .min(1)
        .max(200)
        .parse(integer(single(params, "limit") ?? "100", "limit")),
    ),
  );
  result.set(
    "sort",
    z
      .enum(["name", "size", "added", "posted", "last-check", "next-check", "type", "health"])
      .parse(single(params, "sort") ?? "name"),
  );
  result.set("direction", z.enum(["asc", "desc"]).parse(single(params, "direction") ?? "asc"));
  return result;
}
export function getFilesQueryKey(
  scopePath: string,
  filters: FilesFilters,
  sort: string,
  direction: string,
): string {
  const params = serializeFilesFilters(filters);
  params.set("scopePath", scopePath);
  params.set("sort", sort);
  params.set("direction", direction);
  params.sort();
  return params.toString();
}
