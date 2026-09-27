import { useState } from "react";
import { Button, Toggle } from "~/components/ui";
import type { PrefetchSettings, PrefetchSource } from "./smart-prefetch-model";
import {
  persistedSourceKey,
  type PlexCatalogueLibrary,
  type PlexMediaSection as PlexMediaSectionType,
} from "./plex-source-catalogue";
import {
  setLibraryEnabled,
  setMediaSectionEnabled,
  setSourceEnabled,
} from "./plex-source-selection";

type PlexMediaSectionProps = {
  type: PlexMediaSectionType;
  title: string;
  enabled: boolean;
  libraries: PlexCatalogueLibrary[];
  settings: PrefetchSettings;
  onChange: (settings: PrefetchSettings) => void;
  onCustomize: (source: PrefetchSource) => void;
  onError?: (message: string | null) => void;
};

function controlId(type: string, libraryId: string, suffix: string): string {
  return `plex-${type}-${libraryId || "global"}-${suffix}`.replaceAll(/[^a-zA-Z0-9_-]/g, "-");
}

export function PlexMediaSection({
  type,
  title,
  enabled,
  libraries,
  settings,
  onChange,
  onCustomize,
  onError,
}: PlexMediaSectionProps) {
  const [openLibraries, setOpenLibraries] = useState<Set<string>>(new Set());
  const [openCollections, setOpenCollections] = useState<Set<string>>(new Set());
  const [manualChoice, setManualChoice] = useState<string | null>(null);
  const enabledSources = settings.Sources.filter(
    (source) =>
      source.Enabled &&
      (type === "movie" ? source.Type === "movie" : source.Type !== "movie") &&
      libraries.some(
        (library) =>
          library.enabled &&
          library.identity.serverId === source.ServerId &&
          library.identity.libraryId === source.LibraryId,
      ),
  ).length;
  const toggleOpen = (current: Set<string>, update: (value: Set<string>) => void, key: string) => {
    const next = new Set(current);
    if (next.has(key)) next.delete(key);
    else next.add(key);
    update(next);
  };
  return (
    <section className="space-y-3 rounded-2xl border border-base-content/15 bg-base-200/35 p-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h3 className="text-xl font-semibold">{title}</h3>
          <p className="text-xs text-base-content/60">
            {enabledSources} source{enabledSources === 1 ? "" : "s"} enabled
          </p>
        </div>
        <Toggle
          className="min-h-11"
          label={`Enable ${title} prefetch`}
          checked={enabled}
          onChange={(event) =>
            onChange(setMediaSectionEnabled(settings, type, event.target.checked))
          }
        />
      </div>
      <div className="space-y-2">
        {libraries.map((library) => {
          const key = `${library.identity.libraryId}:${library.identity.type}`;
          const isOpen = openLibraries.has(key);
          const panelId = controlId(type, library.identity.libraryId, "sources");
          const collectionsOpen = openCollections.has(key);
          const collectionsId = controlId(type, library.identity.libraryId, "collections");
          return (
            <div key={key} className="rounded-xl border border-base-content/15 bg-base-100/70">
              <div className="flex min-h-14 flex-wrap items-center gap-2 px-3 py-2">
                <Button
                  className="min-h-11 flex-1 justify-start"
                  variant="ghost"
                  aria-label={`Show ${library.title} sources`}
                  aria-expanded={isOpen}
                  aria-controls={panelId}
                  onClick={() => toggleOpen(openLibraries, setOpenLibraries, key)}
                >
                  {isOpen ? "▾" : "▸"} {library.title}
                </Button>
                <span className="text-xs text-base-content/55">
                  {library.hubs.length} hubs · {library.collections.length} collections
                </span>
                <Toggle
                  className="min-h-11"
                  label={`Enable ${title} library ${library.title}`}
                  checked={library.enabled}
                  onChange={(event) => {
                    const result = setLibraryEnabled(settings, library, event.target.checked);
                    onError?.(result.error);
                    if (result.error) return;
                    onChange(result.settings);
                    if (result.needsManualChoice) {
                      setManualChoice(key);
                      setOpenLibraries(new Set(openLibraries).add(key));
                    } else setManualChoice(null);
                  }}
                />
              </div>
              {isOpen && (
                <div id={panelId} className="space-y-2 border-t border-base-content/10 p-3">
                  {manualChoice === key && (
                    <p className="text-sm text-warning">
                      No recommended source is available. Choose a source below.
                    </p>
                  )}
                  {library.hubs.length === 0 &&
                    library.collections.length === 0 &&
                    library.unavailable.length === 0 && (
                      <p className="text-sm text-base-content/60">
                        No compatible hubs or collections returned by Plex.
                      </p>
                    )}
                  {library.hubs.map((source) => {
                    const persisted = settings.Sources.find(
                      (item) =>
                        persistedSourceKey(item.ServerId, item.Kind, item.Key) ===
                        persistedSourceKey(source.serverId, source.kind, source.key),
                    );
                    return (
                      <div key={source.key} className="flex min-h-11 flex-wrap items-center gap-2">
                        <Toggle
                          className="min-h-11 flex-1"
                          label={`Enable ${library.title} source ${source.title}`}
                          checked={persisted?.Enabled ?? false}
                          onChange={(event) => {
                            const result = setSourceEnabled(
                              settings,
                              library,
                              source,
                              event.target.checked,
                            );
                            onError?.(result.error);
                            if (!result.error) onChange(result.settings);
                          }}
                        />
                        {persisted?.Enabled && (
                          <Button type="button" onClick={() => onCustomize(persisted)}>
                            Customize {source.title}
                          </Button>
                        )}
                      </div>
                    );
                  })}
                  {library.collections.length > 0 && (
                    <div className="rounded-lg border border-base-content/10">
                      <Button
                        className="min-h-11 w-full justify-start"
                        variant="ghost"
                        aria-label={`Show ${library.title} collections`}
                        aria-expanded={collectionsOpen}
                        aria-controls={collectionsId}
                        onClick={() => toggleOpen(openCollections, setOpenCollections, key)}
                      >
                        {collectionsOpen ? "▾" : "▸"} Collections ({library.collections.length})
                      </Button>
                      {collectionsOpen && (
                        <div
                          id={collectionsId}
                          className="space-y-1 border-t border-base-content/10 p-2"
                        >
                          {library.collections.map((source) => {
                            const persisted = settings.Sources.find(
                              (item) =>
                                persistedSourceKey(item.ServerId, item.Kind, item.Key) ===
                                persistedSourceKey(source.serverId, source.kind, source.key),
                            );
                            return (
                              <div
                                key={source.key}
                                className="flex min-h-11 flex-wrap items-center gap-2"
                              >
                                <Toggle
                                  className="min-h-11 flex-1"
                                  label={`Enable ${library.title} collection ${source.title}`}
                                  checked={persisted?.Enabled ?? false}
                                  onChange={(event) => {
                                    const result = setSourceEnabled(
                                      settings,
                                      library,
                                      source,
                                      event.target.checked,
                                    );
                                    onError?.(result.error);
                                    if (!result.error) onChange(result.settings);
                                  }}
                                />
                                {persisted?.Enabled && (
                                  <Button type="button" onClick={() => onCustomize(persisted)}>
                                    Customize {source.title}
                                  </Button>
                                )}
                              </div>
                            );
                          })}
                        </div>
                      )}
                    </div>
                  )}
                  {library.unavailable.length > 0 && (
                    <div className="space-y-1 rounded-lg border border-warning/30 p-3">
                      <p className="text-sm font-medium">Saved but not currently available</p>
                      {library.unavailable.map((source) => (
                        <Toggle
                          className="min-h-11"
                          key={`${source.Kind}:${source.Key}`}
                          label={`Enable ${library.title} unavailable source ${source.Title}`}
                          checked={source.Enabled}
                          onChange={(event) =>
                            onChange({
                              ...settings,
                              Sources: settings.Sources.map((item) =>
                                item === source ? { ...item, Enabled: event.target.checked } : item,
                              ),
                            })
                          }
                        />
                      ))}
                    </div>
                  )}
                </div>
              )}
            </div>
          );
        })}
      </div>
    </section>
  );
}
