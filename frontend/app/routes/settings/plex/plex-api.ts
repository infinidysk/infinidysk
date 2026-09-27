import { withUrlBase } from "~/utils/url-base";

export type PlexAccount = { id: string; name: string; token: string };
export type PlexMapping = { plexPath: string; davPath?: string | null; localPath?: string | null };
export type PlexServer = {
  id: string;
  name: string;
  url: string;
  token: string;
  enabled: boolean;
  accountId?: string | null;
  pathMappings: PlexMapping[];
  handle?: string;
};
type PlexServersSubscriber = (servers: PlexServer[]) => void;
const plexServersSubscribers = new Set<PlexServersSubscriber>();

export function publishPlexServers(servers: PlexServer[]) {
  for (const subscriber of plexServersSubscribers) subscriber(servers);
}

export function subscribePlexServers(subscriber: PlexServersSubscriber) {
  plexServersSubscribers.add(subscriber);
  return () => {
    plexServersSubscribers.delete(subscriber);
  };
}
export type PlexLogin = { handle: string; url: string; expiresAt: string };
export type PlexCandidate = {
  handle: string;
  id: string;
  name: string;
  connections: { uri: string; local: boolean; relay: boolean }[];
};
export type PlexHomeUser = { id: string; name: string; protected: boolean; admin: boolean };
export type PlexLibrary = { id: string; title: string; type: string };
export type PlexUser = { id: string; name: string };
export type PlexSource = {
  serverId: string;
  libraryId: string | null;
  kind: string;
  id: string;
  key: string;
  title: string;
  type: string;
};
export type PlexMedia = {
  ratingKey: string;
  title: string;
  type: string;
  file: string | null;
  mappingStatus?: string;
  mappingReason?: string;
};
export type PlexSnapshot<T> = {
  data: T[];
  lastSuccess: string | null;
  isStale: boolean;
  error: string | null;
};

export async function plexRequest<T>(
  operation: string,
  body: object = {},
  signal?: AbortSignal,
): Promise<T> {
  const response = await fetch(withUrlBase(`/api/plex/${operation}`), {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
    ...(signal ? { signal } : {}),
  });
  if (!response.ok)
    throw new Error(
      response.status === 401
        ? "Plex session expired. Reload and sign in again."
        : response.status === 409
          ? "Plex settings or login changed. Reload and retry."
          : "Plex request failed. Check authorization, connection, and settings.",
    );
  return (await response.json()) as T;
}

let bootstrap: Promise<{ accounts: PlexAccount[]; servers: PlexServer[] }> | null = null;
export function loadPlexBootstrap() {
  // Coalesce StrictMode/remount callers; never race first-response owner-cookie provisioning.
  bootstrap ??= (async () => {
    const accounts = await plexRequest<{ accounts: PlexAccount[] }>("accounts");
    const servers = await plexRequest<{ servers: PlexServer[] }>("servers");
    return { ...accounts, ...servers };
  })().finally(() => {
    bootstrap = null;
  });
  return bootstrap;
}

function absolute(path: string | null | undefined): boolean {
  return Boolean(
    path &&
    path.length <= 4096 &&
    (/^\//.test(path) || /^[A-Za-z]:[/\\]/.test(path)) &&
    !path.split(/[/\\]/).some((part) => part === "." || part === "..") &&
    ![...path].some((character) => character.charCodeAt(0) < 32 || character.charCodeAt(0) === 127),
  );
}
export function validMappings(mappings: PlexMapping[]): boolean {
  return (
    mappings.length <= 64 &&
    mappings.every(
      (mapping) =>
        absolute(mapping.plexPath) &&
        Boolean(mapping.davPath) !== Boolean(mapping.localPath) &&
        (mapping.davPath
          ? mapping.davPath.startsWith("/") && absolute(mapping.davPath)
          : absolute(mapping.localPath)),
    )
  );
}
export function validServer(server: PlexServer): boolean {
  try {
    const url = new URL(server.url);
    return Boolean(
      server.name.trim() &&
      (server.token || server.handle) &&
      ["http:", "https:"].includes(url.protocol) &&
      !url.username &&
      !url.password &&
      !url.search &&
      !url.hash &&
      validMappings(server.pathMappings),
    );
  } catch {
    return false;
  }
}
export function serverSaveRequest(server: PlexServer) {
  // Provenance is assigned by the backend, not by a browser-supplied accountId.
  return {
    id: server.id,
    name: server.name,
    url: server.url,
    token: server.token,
    enabled: server.enabled,
    pathMappings: server.pathMappings,
    ...(server.handle ? { handle: server.handle } : {}),
  };
}
