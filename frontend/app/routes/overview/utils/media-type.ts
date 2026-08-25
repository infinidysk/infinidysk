export type MediaType = "movie" | "episode";

// "S01E03" / "s2e11" season-episode tokens, delimited so codec names like
// "x264" never match.
const EPISODE_PATTERN = /(?:^|[\s._-])s\d{1,2}e\d{1,2}(?:[\s._-]|$)/i;
// "1x02" season-x-episode tokens.
const EPISODE_ALT_PATTERN = /(?:^|[\s._-])\d{1,2}x\d{2}(?:[\s._-]|$)/;
const YEAR_PATTERN = /(?:^|[\s._([-])(19\d{2}|20\d{2})(?:[\s._)\]-]|$)/;
const QUALITY_PATTERN =
  /\b(480p|576p|720p|1080p|2160p|4320p|blu[-. ]?ray|bdrip|web[-. ]?dl|webrip|hdrip|hdtv|dvdrip|remux)\b/i;

/**
 * Best-effort media classification from a release-style file name, used for
 * the Right Now row badge. Returns null when the name doesn't look like
 * media — non-media files get no badge rather than a wrong one.
 */
export function mediaTypeFromFileName(fileName: string): MediaType | null {
  if (EPISODE_PATTERN.test(fileName) || EPISODE_ALT_PATTERN.test(fileName)) return "episode";
  if (YEAR_PATTERN.test(fileName) && QUALITY_PATTERN.test(fileName)) return "movie";
  return null;
}
