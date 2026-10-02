import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Alert } from "~/components/ui";
import {
  loadPlexBootstrap,
  plexRequest,
  subscribePlexServers,
  type PlexLibrary,
  type PlexMedia,
  type PlexServer,
  type PlexSnapshot,
  type PlexSource,
  type PlexUser,
} from "../plex/plex-api";
import { buildPlexSourceCatalogue, persistedSourceKey } from "./plex-source-catalogue";
import { PlexMediaSection } from "./plex-media-section";
import { PlexSourceCustomization } from "./plex-source-customization";
import { PlexSourceToolbar } from "./plex-source-toolbar";
import type { PrefetchSettings, PrefetchSource } from "./smart-prefetch-model";

type SourceRequest = { libraryId: string | null };

function sourceRequestMatches(source: PlexSource, request: SourceRequest): boolean {
  return (source.libraryId ?? null) === request.libraryId;
}

function latestSuccess(snapshots: Array<PlexSnapshot<unknown> | null>): string | null {
  return (
    snapshots
      .map((snapshot) => snapshot?.lastSuccess ?? null)
      .filter((value): value is string => value !== null)
      .sort()
      .at(-1) ?? null
  );
}

export function PlexSources({
  settings,
  onChange,
}: {
  settings: PrefetchSettings;
  onChange: (settings: PrefetchSettings) => void;
}) {
  const [servers, setServers] = useState<PlexServer[]>([]);
  const [serverId, setServerId] = useState("");
  const [libraries, setLibraries] = useState<PlexSnapshot<PlexLibrary> | null>(null);
  const [users, setUsers] = useState<PlexSnapshot<PlexUser> | null>(null);
  const [sources, setSources] = useState<PlexSnapshot<PlexSource> | null>(null);
  const [customizing, setCustomizing] = useState<string | null>(null);
  const [preview, setPreview] = useState<{ key: string; items: PlexMedia[] } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const generation = useRef(0);
  const enabledServers = useMemo(() => servers.filter((server) => server.enabled), [servers]);

  useEffect(() => {
    let alive = true;
    void loadPlexBootstrap()
      .then((result) => {
        if (alive) setServers(result.servers);
      })
      .catch((cause) => {
        if (alive) setError(cause instanceof Error ? cause.message : "Plex servers unavailable.");
      });
    return () => {
      alive = false;
    };
  }, []);

  useEffect(() => subscribePlexServers(setServers), []);

  useEffect(() => {
    if (!serverId || enabledServers.some((server) => server.id === serverId)) return;
    generation.current++;
    setServerId("");
    setLibraries(null);
    setUsers(null);
    setSources(null);
    setPreview(null);
    setCustomizing(null);
  }, [serverId, enabledServers]);

  const refresh = useCallback(
    async (forceRefresh = false) => {
      if (!serverId) return;
      const version = ++generation.current;
      setBusy(true);
      setError(null);
      try {
        const body = { serverId, forceRefresh };
        const [nextLibraries, nextUsers] = await Promise.all([
          plexRequest<PlexSnapshot<PlexLibrary>>("libraries", body),
          plexRequest<PlexSnapshot<PlexUser>>("users", body),
        ]);
        const requests: SourceRequest[] = [
          { libraryId: null },
          ...nextLibraries.data
            .filter((library) => library.type === "movie" || library.type === "show")
            .map((library) => ({ libraryId: library.id })),
        ];
        const results = await Promise.allSettled(
          requests.map((request) =>
            plexRequest<PlexSnapshot<PlexSource>>("sources", {
              serverId,
              libraryId: request.libraryId,
              forceRefresh,
            }),
          ),
        );
        if (version !== generation.current) return;
        const merged: PlexSource[] = [];
        const failures: string[] = [];
        let isStale = false;
        let lastSuccess: string | null = null;
        results.forEach((result, index) => {
          const request = requests[index]!;
          if (result.status === "fulfilled") {
            merged.push(...result.value.data);
            isStale ||= result.value.isStale;
            if (result.value.error) failures.push(result.value.error);
            if (!lastSuccess || (result.value.lastSuccess ?? "") > lastSuccess)
              lastSuccess = result.value.lastSuccess;
          } else {
            isStale = true;
            failures.push(
              result.reason instanceof Error ? result.reason.message : "Source unavailable.",
            );
            if (sources)
              merged.push(
                ...sources.data.filter((source) => sourceRequestMatches(source, request)),
              );
          }
        });
        setLibraries(nextLibraries);
        setUsers(nextUsers);
        setSources({
          data: merged,
          lastSuccess: lastSuccess ?? sources?.lastSuccess ?? null,
          isStale,
          error: failures.length > 0 ? [...new Set(failures)].join(" · ") : null,
        });
      } catch (cause) {
        if (version === generation.current) {
          setError(cause instanceof Error ? cause.message : "Plex catalogue unavailable.");
          setLibraries((current) => (current ? { ...current, isStale: true } : current));
          setUsers((current) => (current ? { ...current, isStale: true } : current));
          setSources((current) => (current ? { ...current, isStale: true } : current));
        }
      } finally {
        if (version === generation.current) setBusy(false);
      }
    },
    [serverId, sources],
  );

  useEffect(() => {
    if (!serverId) return;
    const requestGeneration = generation;
    void refresh();
    return () => {
      requestGeneration.current++;
    };
    // `refresh` also reads retained source snapshots; server changes alone start automatic loads.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [serverId]);

  const catalogue = useMemo(
    () =>
      buildPlexSourceCatalogue({
        serverId,
        libraries: libraries?.data ?? [],
        sources: sources?.data ?? [],
        savedSources: settings.Sources,
        disabledLibraries: settings.DisabledLibraries,
      }),
    [serverId, libraries, sources, settings.Sources, settings.DisabledLibraries],
  );
  const customizedSource = customizing
    ? settings.Sources.find(
        (source) => persistedSourceKey(source.ServerId, source.Kind, source.Key) === customizing,
      )
    : undefined;

  const updateSource = (next: PrefetchSource) =>
    onChange({
      ...settings,
      Sources: settings.Sources.map((source) =>
        persistedSourceKey(source.ServerId, source.Kind, source.Key) ===
        persistedSourceKey(next.ServerId, next.Kind, next.Key)
          ? next
          : source,
      ),
    });

  const showPreview = async (source: PrefetchSource) => {
    const key = persistedSourceKey(source.ServerId, source.Kind, source.Key);
    setBusy(true);
    setError(null);
    try {
      const result = await plexRequest<{ items: PlexMedia[] }>("preview", {
        serverId: source.ServerId,
        key: source.Key,
        limit: Math.min(source.Limit, 20),
      });
      setPreview({ key, items: result.items });
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Preview unavailable.");
    } finally {
      setBusy(false);
    }
  };

  const snapshots: Array<PlexSnapshot<unknown> | null> = [libraries, users, sources];
  const stale = snapshots.some((snapshot) => snapshot?.isStale);
  const snapshotError = snapshots.map((snapshot) => snapshot?.error).find(Boolean) ?? null;
  return (
    <div className="space-y-4">
      {error && <Alert variant="danger">{error}</Alert>}
      <PlexSourceToolbar
        servers={enabledServers}
        serverId={serverId}
        users={users?.data ?? []}
        selectedUsers={settings.Users}
        busy={busy}
        lastSuccess={latestSuccess(snapshots)}
        stale={stale}
        error={snapshotError}
        onServerChange={(next) => {
          generation.current++;
          setServerId(next);
          setLibraries(null);
          setUsers(null);
          setSources(null);
          setPreview(null);
          setCustomizing(null);
        }}
        onUsersChange={(next) => onChange({ ...settings, Users: next })}
        onRefresh={() => void refresh(true)}
      />
      {!serverId && (
        <Alert variant="info">
          Connect a Plex server first, then choose it above to configure prefetch sources.
        </Alert>
      )}
      {serverId && libraries && catalogue.movie.length === 0 && catalogue.show.length === 0 && (
        <Alert variant="info">Only movie and TV libraries can be used for Smart Prefetch.</Alert>
      )}
      {serverId && (catalogue.movie.length > 0 || catalogue.show.length > 0) && (
        <div className="grid gap-4 xl:grid-cols-2">
          <PlexMediaSection
            type="movie"
            title="Movies"
            enabled={settings.MovieEnabled}
            libraries={catalogue.movie}
            settings={settings}
            onChange={onChange}
            onCustomize={(source) =>
              setCustomizing(persistedSourceKey(source.ServerId, source.Kind, source.Key))
            }
            onError={setError}
          />
          <PlexMediaSection
            type="show"
            title="TV shows"
            enabled={settings.TvEnabled}
            libraries={catalogue.show}
            settings={settings}
            onChange={onChange}
            onCustomize={(source) =>
              setCustomizing(persistedSourceKey(source.ServerId, source.Kind, source.Key))
            }
            onError={setError}
          />
        </div>
      )}
      {customizedSource && (
        <PlexSourceCustomization
          source={customizedSource}
          preview={preview?.key === customizing ? preview.items : null}
          busy={busy}
          onChange={updateSource}
          onPreview={() => void showPreview(customizedSource)}
          onClose={() => setCustomizing(null)}
        />
      )}
    </div>
  );
}
