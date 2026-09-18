import { useCallback, useRef, useState, type Dispatch, type SetStateAction } from "react";
import { Button } from "~/components/ui/button";
import { Alert, Spinner } from "~/components/ui/feedback";
import { Input, Toggle } from "~/components/ui/form";
import { Icon } from "~/components/ui/icon";
import { ManagedSetting, SettingsCard, SettingsIntro, SettingsPage } from "~/components/ui";
import { isMaskedSecret } from "~/utils/config-mask";
import { generateUuid } from "~/utils/uuid";
import { withUrlBase } from "~/utils/url-base";

const CONFIG_KEY = "media-servers.instances";

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

type MediaServersSettingsProps = {
  config: Record<string, string>;
  setNewConfig: Dispatch<SetStateAction<Record<string, string>>>;
};

type TestState = {
  status: "idle" | "testing" | "success" | "error";
  error: string | null;
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

export function isMediaServersSettingsUpdated(
  config: Record<string, string>,
  newConfig: Record<string, string>,
): boolean {
  return config[CONFIG_KEY] !== newConfig[CONFIG_KEY];
}

export function isMediaServersSettingsValid(config: Record<string, string>): boolean {
  const media = parseMediaServerConfig(config[CONFIG_KEY]);
  const ids = new Set<string>();

  return media.Instances.every((instance) => {
    if (!instance.Id || ids.has(instance.Id)) return false;
    ids.add(instance.Id);

    if (!instance.Name.trim() || !isSafeHttpBaseUrl(instance.BaseUrl)) return false;
    if (instance.Enabled && !instance.Token.trim()) return false;
    return instance.PathMappings.every(
      (mapping) =>
        typeof mapping?.MediaServerPrefix === "string" &&
        typeof mapping?.InfiniDyskPrefix === "string" &&
        mapping.MediaServerPrefix.trim().length > 0 &&
        mapping.InfiniDyskPrefix.trim().startsWith("/"),
    );
  });
}

export function MediaServersSettings({ config, setNewConfig }: MediaServersSettingsProps) {
  const media = parseMediaServerConfig(config[CONFIG_KEY]);
  const [tests, setTests] = useState<Record<string, TestState>>({});
  const generations = useRef<Record<string, number>>({});

  const updateConfig = useCallback(
    (next: MediaServerConfig) => {
      setNewConfig({ ...config, [CONFIG_KEY]: JSON.stringify(next) });
    },
    [config, setNewConfig],
  );

  const resetTest = useCallback((id: string) => {
    generations.current[id] = (generations.current[id] ?? 0) + 1;
    setTests((current) => ({ ...current, [id]: { status: "idle", error: null } }));
  }, []);

  const addInstance = useCallback(() => {
    updateConfig({
      Instances: [
        ...media.Instances,
        {
          Id: generateUuid(),
          Type: "Plex",
          Name: "Plex",
          BaseUrl: "",
          Token: "",
          Enabled: true,
          PathMappings: [],
        },
      ],
    });
  }, [media, updateConfig]);

  const updateInstance = useCallback(
    <K extends keyof MediaServerInstance>(
      index: number,
      field: K,
      value: MediaServerInstance[K],
    ) => {
      const instance = media.Instances[index];
      if (!instance) return;
      resetTest(instance.Id);
      updateConfig({
        Instances: media.Instances.map((entry, current) =>
          current === index ? { ...entry, [field]: value } : entry,
        ),
      });
    },
    [media, resetTest, updateConfig],
  );

  const removeInstance = useCallback(
    (index: number) => {
      const instance = media.Instances[index];
      if (instance) resetTest(instance.Id);
      updateConfig({ Instances: media.Instances.filter((_, current) => current !== index) });
    },
    [media, resetTest, updateConfig],
  );

  const addMapping = useCallback(
    (index: number) => {
      const instance = media.Instances[index];
      if (!instance) return;
      updateInstance(index, "PathMappings", [
        ...instance.PathMappings,
        { MediaServerPrefix: "", InfiniDyskPrefix: "" },
      ]);
    },
    [media, updateInstance],
  );

  const updateMapping = useCallback(
    (
      instanceIndex: number,
      mappingIndex: number,
      field: keyof MediaServerPathMapping,
      value: string,
    ) => {
      const instance = media.Instances[instanceIndex];
      if (!instance) return;
      updateInstance(
        instanceIndex,
        "PathMappings",
        instance.PathMappings.map((mapping, current) =>
          current === mappingIndex ? { ...mapping, [field]: value } : mapping,
        ),
      );
    },
    [media, updateInstance],
  );

  const removeMapping = useCallback(
    (instanceIndex: number, mappingIndex: number) => {
      const instance = media.Instances[instanceIndex];
      if (!instance) return;
      updateInstance(
        instanceIndex,
        "PathMappings",
        instance.PathMappings.filter((_, current) => current !== mappingIndex),
      );
    },
    [media, updateInstance],
  );

  const testConnection = useCallback(async (instance: MediaServerInstance) => {
    const generation = (generations.current[instance.Id] ?? 0) + 1;
    generations.current[instance.Id] = generation;
    setTests((current) => ({
      ...current,
      [instance.Id]: { status: "testing", error: null },
    }));

    try {
      const form = new FormData();
      form.append("type", instance.Type);
      form.append("baseUrl", instance.BaseUrl);
      form.append("token", instance.Token);

      const response = await fetch(withUrlBase("/api/test-media-server-connection"), {
        method: "POST",
        body: form,
      });
      const result = (await response.json()) as {
        status?: boolean;
        connected?: boolean;
        error?: string | null;
      };
      if (generations.current[instance.Id] !== generation) return;

      if (response.ok && result.status && result.connected) {
        setTests((current) => ({
          ...current,
          [instance.Id]: { status: "success", error: null },
        }));
      } else {
        setTests((current) => ({
          ...current,
          [instance.Id]: {
            status: "error",
            error: result.error || "Connection test failed",
          },
        }));
      }
    } catch {
      if (generations.current[instance.Id] !== generation) return;
      setTests((current) => ({
        ...current,
        [instance.Id]: { status: "error", error: "Connection test failed" },
      }));
    }
  }, []);

  return (
    <SettingsPage>
      <SettingsIntro>
        Connect Plex, Emby, or Jellyfin so Right now can use the media server&apos;s actual player
        sessions for playback state and viewer progress. InfiniDysk read activity remains separate
        transport telemetry.
      </SettingsIntro>

      <ManagedSetting configKey={CONFIG_KEY}>
        <div className="flex flex-col gap-4">
          <div className="flex justify-end">
            <Button size="small" onClick={addInstance}>
              <Icon name="add" className="!text-[18px]" />
              Add media server
            </Button>
          </div>

          {media.Instances.length === 0 ? (
            <Alert variant="info" className="text-sm">
              No media servers configured. Add Plex, Emby, or Jellyfin to enable authoritative
              external playback sessions in Right now.
            </Alert>
          ) : (
            media.Instances.map((instance, index) => {
              const test = tests[instance.Id] ?? { status: "idle", error: null };
              const tokenIsSaved = isMaskedSecret(instance.Token);
              const canTest =
                instance.BaseUrl.trim().length > 0 && instance.Token.trim().length > 0;

              return (
                <SettingsCard
                  key={instance.Id}
                  icon="live_tv"
                  title={instance.Name.trim() || instance.Type}
                  description="Authoritative playback source"
                  action={
                    <Button
                      size="small"
                      variant="ghost"
                      aria-label={`Remove ${instance.Name || instance.Type}`}
                      onClick={() => removeInstance(index)}
                    >
                      <Icon name="delete" className="!text-[18px]" />
                    </Button>
                  }
                >
                  <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
                    <div className="space-y-2">
                      <label
                        className="block text-sm font-medium"
                        htmlFor={`media-server-type-${instance.Id}`}
                      >
                        Type
                      </label>
                      <select
                        id={`media-server-type-${instance.Id}`}
                        className="select w-full"
                        value={instance.Type}
                        onChange={(event) =>
                          updateInstance(index, "Type", event.target.value as MediaServerType)
                        }
                      >
                        <option value="Plex">Plex</option>
                        <option value="Emby">Emby</option>
                        <option value="Jellyfin">Jellyfin</option>
                      </select>
                    </div>

                    <div className="space-y-2">
                      <label
                        className="block text-sm font-medium"
                        htmlFor={`media-server-name-${instance.Id}`}
                      >
                        Name
                      </label>
                      <Input
                        id={`media-server-name-${instance.Id}`}
                        value={instance.Name}
                        onChange={(event) => updateInstance(index, "Name", event.target.value)}
                        placeholder="Home Plex"
                      />
                    </div>

                    <div className="space-y-2 lg:col-span-2">
                      <label
                        className="block text-sm font-medium"
                        htmlFor={`media-server-url-${instance.Id}`}
                      >
                        Base URL
                      </label>
                      <Input
                        id={`media-server-url-${instance.Id}`}
                        value={instance.BaseUrl}
                        onChange={(event) => updateInstance(index, "BaseUrl", event.target.value)}
                        placeholder={
                          instance.Type === "Plex" ? "http://plex:32400" : "http://jellyfin:8096"
                        }
                      />
                      <p className="text-[11px] leading-relaxed text-base-content/45">
                        Use a URL reachable from the InfiniDysk container. Redirects are rejected so
                        authentication headers cannot be forwarded to another host.
                      </p>
                    </div>

                    <div className="space-y-2 lg:col-span-2">
                      <label
                        className="block text-sm font-medium"
                        htmlFor={`media-server-token-${instance.Id}`}
                      >
                        Token / API key
                      </label>
                      <Input
                        id={`media-server-token-${instance.Id}`}
                        type="password"
                        value={tokenIsSaved ? "" : instance.Token}
                        placeholder={tokenIsSaved ? "Saved token" : "Token or API key"}
                        onChange={(event) => updateInstance(index, "Token", event.target.value)}
                        autoComplete="off"
                      />
                      {tokenIsSaved && (
                        <p className="text-[11px] leading-relaxed text-base-content/45">
                          A saved credential is present and remains masked. Leave this field empty
                          to keep it, or type a new value to replace it.
                        </p>
                      )}
                    </div>

                    <div className="flex flex-wrap items-center gap-3 lg:col-span-2">
                      <Toggle
                        id={`media-server-enabled-${instance.Id}`}
                        checked={instance.Enabled}
                        onChange={(event) => updateInstance(index, "Enabled", event.target.checked)}
                        label={<span className="text-sm">Enable playback polling</span>}
                      />
                      <Button
                        size="small"
                        variant={
                          test.status === "success"
                            ? "success"
                            : test.status === "error"
                              ? "danger"
                              : "outline"
                        }
                        disabled={!canTest || test.status === "testing"}
                        onClick={() => void testConnection(instance)}
                      >
                        {test.status === "testing" ? (
                          <Spinner />
                        ) : test.status === "success" ? (
                          <Icon name="check" className="!text-[18px]" />
                        ) : test.status === "error" ? (
                          <Icon name="close" className="!text-[18px]" />
                        ) : (
                          <Icon name="wifi_tethering" className="!text-[18px]" />
                        )}
                        {test.status === "testing" ? "Testing" : "Test Connection"}
                      </Button>
                    </div>

                    {test.status === "success" && (
                      <Alert variant="success" className="text-xs lg:col-span-2">
                        Connection and authentication successful.
                      </Alert>
                    )}
                    {test.status === "error" && test.error && (
                      <Alert variant="danger" className="text-xs lg:col-span-2">
                        {test.error}
                      </Alert>
                    )}

                    <div className="space-y-3 lg:col-span-2">
                      <div className="flex flex-wrap items-center justify-between gap-2">
                        <div>
                          <h4 className="text-sm font-semibold">Path mappings</h4>
                          <p className="text-[11px] text-base-content/45">
                            Optional exact prefix translations from paths reported by the media
                            server to paths visible inside InfiniDysk. No fuzzy library matching is
                            performed.
                          </p>
                        </div>
                        <Button size="small" variant="outline" onClick={() => addMapping(index)}>
                          <Icon name="add" className="!text-[18px]" />
                          Add mapping
                        </Button>
                      </div>

                      {instance.PathMappings.map((mapping, mappingIndex) => (
                        <div
                          key={`${instance.Id}-mapping-${mappingIndex}`}
                          className="grid grid-cols-1 gap-2 rounded-box border border-base-content/10 p-3 lg:grid-cols-[1fr_1fr_auto]"
                        >
                          <Input
                            aria-label="Media server path prefix"
                            value={mapping.MediaServerPrefix}
                            onChange={(event) =>
                              updateMapping(
                                index,
                                mappingIndex,
                                "MediaServerPrefix",
                                event.target.value,
                              )
                            }
                            placeholder="/media/movies"
                          />
                          <Input
                            aria-label="InfiniDysk path prefix"
                            value={mapping.InfiniDyskPrefix}
                            onChange={(event) =>
                              updateMapping(
                                index,
                                mappingIndex,
                                "InfiniDyskPrefix",
                                event.target.value,
                              )
                            }
                            placeholder="/mnt/media/movies"
                          />
                          <Button
                            size="small"
                            variant="ghost"
                            aria-label="Remove path mapping"
                            onClick={() => removeMapping(index, mappingIndex)}
                          >
                            <Icon name="delete" className="!text-[18px]" />
                          </Button>
                        </div>
                      ))}
                    </div>
                  </div>
                </SettingsCard>
              );
            })
          )}
        </div>
      </ManagedSetting>
    </SettingsPage>
  );
}

function isMediaServerInstance(value: unknown): value is MediaServerInstance {
  if (value === null || typeof value !== "object") return false;
  const instance = value as Record<string, unknown>;
  return (
    typeof instance.Id === "string" &&
    typeof instance.Name === "string" &&
    typeof instance.BaseUrl === "string" &&
    typeof instance.Token === "string" &&
    typeof instance.Enabled === "boolean" &&
    isMediaServerType(instance.Type) &&
    Array.isArray(instance.PathMappings)
  );
}

function isMediaServerType(value: unknown): value is MediaServerType {
  return value === "Plex" || value === "Emby" || value === "Jellyfin";
}

function isSafeHttpBaseUrl(value: string): boolean {
  try {
    const url = new URL(value);
    return (
      (url.protocol === "http:" || url.protocol === "https:") &&
      url.username === "" &&
      url.password === "" &&
      url.search === "" &&
      url.hash === ""
    );
  } catch {
    return false;
  }
}
