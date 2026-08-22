import { URL_BASE } from "~/utils/url-base";

export type ProfileAdapterKey = "json" | "addon" | "newznab";

const paths: Record<ProfileAdapterKey, (token: string) => string> = {
  json: (token) => `/api/search/${token}/lookup?type=movie&id=tt0111161`,
  newznab: (token) => `/adapters/newznab/${token}/api`,
  addon: (token) => `/adapters/addon/${token}/manifest.json`,
};

export function buildProfileAdapterUrl(
  origin: string,
  adapter: ProfileAdapterKey,
  token: string,
  urlBase = URL_BASE,
): string {
  return `${origin}${urlBase}${paths[adapter](token)}`;
}
