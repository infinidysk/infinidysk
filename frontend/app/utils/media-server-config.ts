export type MediaServerType = "Plex" | "Emby" | "Jellyfin";

export type MediaServerPathMapping = {
  MediaServerPrefix: string;
  InfiniDyskPrefix: string;
};

export type MediaServerInstance = {
  Id: string;
  Type: MediaServerType;
  Name: string;
  BaseUrl: string;
  Token: string;
  Enabled: boolean;
  PathMappings: MediaServerPathMapping[];
};

export type MediaServerConfig = {
  Instances: MediaServerInstance[];
};

export function parseMediaServerConfig(value: string | undefined): MediaServerConfig {
  if (!value) return { Instances: [] };
  try {
    const parsed = JSON.parse(value) as Partial<MediaServerConfig> | null;
    if (!parsed || !Array.isArray(parsed.Instances)) return { Instances: [] };

    const candidates: unknown[] = parsed.Instances;
    return { Instances: candidates.filter(isMediaServerInstance) };
  } catch {
    return { Instances: [] };
  }
}

function isMediaServerInstance(value: unknown): value is MediaServerInstance {
  if (value === null || typeof value !== "object") return false;
  const instance = value as Record<string, unknown>;
  return (
    typeof instance["Id"] === "string" &&
    typeof instance["Name"] === "string" &&
    typeof instance["BaseUrl"] === "string" &&
    typeof instance["Token"] === "string" &&
    typeof instance["Enabled"] === "boolean" &&
    isMediaServerType(instance["Type"]) &&
    Array.isArray(instance["PathMappings"])
  );
}

function isMediaServerType(value: unknown): value is MediaServerType {
  return value === "Plex" || value === "Emby" || value === "Jellyfin";
}
