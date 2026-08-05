import type { Config } from "@react-router/dev/config";

/**
 * Normalizes URL_BASE for use as a React Router basename: anything truthy and not "/"
 * is forced into "/path" form (single leading slash, no trailing slash). Empty or "/"
 * yields "/" which is React Router's default. Mirror of normalizeUrlBase in
 * `app/utils/url-base.ts` (this file runs outside the Vite app graph) — keep in sync.
 */
function normalizeBasename(raw: string | undefined): string {
  if (!raw) return "/";
  const trimmed = raw.trim();
  if (trimmed === "" || trimmed === "/") return "/";
  const withLeading = trimmed.startsWith("/") ? trimmed : `/${trimmed}`;
  const withoutTrailing = withLeading.replace(/\/+$/, "");
  return withoutTrailing || "/";
}

export default {
  // Server-side render by default, to enable SPA mode set this to `false`
  ssr: true,
  // URL_BASE env var, read at build time, controls the React Router basename so
  // <Link> and useFetcher generate the correct paths when the app is hosted under
  // a sub-path. Must match the URL_BASE env var at runtime (see server.ts).
  basename: normalizeBasename(process.env.URL_BASE),
} satisfies Config;
