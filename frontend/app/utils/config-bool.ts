/**
 * Parse a stored config flag. Unset, blank, and invalid values use `fallback`.
 * Matching is trimmed and case-insensitive, same as .NET `bool.TryParse`.
 */
export function parseConfigBoolean(value: string | undefined | null, fallback = true): boolean {
  if (value == null) return fallback;
  const trimmed = value.trim();
  if (trimmed === "") return fallback;
  const normalized = trimmed.toLowerCase();
  if (normalized === "true") return true;
  if (normalized === "false") return false;
  return fallback;
}
