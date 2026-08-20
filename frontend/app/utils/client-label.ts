/** Known WebDAV / media clients matched by case-insensitive UA substring. */
const KNOWN_CLIENTS: readonly { match: string; label: string }[] = [
  { match: "rclone", label: "rclone" },
  { match: "plex", label: "Plex" },
  { match: "emby", label: "Emby" },
  { match: "jellyfin", label: "Jellyfin" },
  { match: "infuse", label: "Infuse" },
  { match: "vlc", label: "VLC" },
  { match: "kodi", label: "Kodi" },
];

const UNKNOWN = "Unknown";
const TRUNCATE_LEN = 28;

/**
 * Short display label for an Active Reads client, derived from User-Agent.
 */
export function clientLabelFromUserAgent(userAgent: string | null | undefined): string {
  const ua = userAgent?.trim();
  if (!ua) return UNKNOWN;

  const lower = ua.toLowerCase();
  for (const { match, label } of KNOWN_CLIENTS) {
    if (lower.includes(match)) return label;
  }

  return ua.length <= TRUNCATE_LEN ? ua : ua.slice(0, TRUNCATE_LEN - 1) + "…";
}

/** Tooltip text: full UA and/or IP when present. */
export function clientIdentityTooltip(
  userAgent: string | null | undefined,
  clientIp: string | null | undefined,
): string | undefined {
  const parts: string[] = [];
  const ua = userAgent?.trim();
  const ip = clientIp?.trim();
  if (ua) parts.push(ua);
  if (ip) parts.push(ip);
  return parts.length > 0 ? parts.join(" · ") : undefined;
}
