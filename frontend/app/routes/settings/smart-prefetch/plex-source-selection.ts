import type { PlexSource } from "../plex/plex-api";
import type { PrefetchSettings, PrefetchSource } from "./smart-prefetch-model";
import {
  mediaSection,
  persistedSourceKey,
  recommendationFor,
  type PlexCatalogueLibrary,
  type PlexLibraryIdentity,
  type PlexMediaSection,
} from "./plex-source-catalogue";

const sourceLimit = 128;
type MutationResult = { settings: PrefetchSettings; error: string | null };
export type LibraryMutationResult = MutationResult & { needsManualChoice: boolean };

function sameLibrary(source: PrefetchSource, identity: PlexLibraryIdentity): boolean {
  return (
    source.ServerId === identity.serverId &&
    source.LibraryId === identity.libraryId &&
    mediaSection(source.Type) === identity.type
  );
}

function withoutLibraryGate(
  settings: PrefetchSettings,
  identity: PlexLibraryIdentity,
): PrefetchSettings {
  return {
    ...settings,
    DisabledLibraries: settings.DisabledLibraries.filter(
      (library) =>
        library.ServerId !== identity.serverId ||
        library.LibraryId !== identity.libraryId ||
        library.Type !== identity.type,
    ),
  };
}

function toPrefetchSource(library: PlexCatalogueLibrary, source: PlexSource): PrefetchSource {
  return {
    ServerId: source.serverId,
    LibraryId: source.libraryId ?? library.identity.libraryId,
    Kind: source.kind,
    Key: source.key,
    Title: source.title,
    Type: library.identity.type,
    Enabled: true,
    Limit: 10,
    ExcludedShows: [],
  };
}

export function setMediaSectionEnabled(
  settings: PrefetchSettings,
  type: PlexMediaSection,
  enabled: boolean,
): PrefetchSettings {
  return type === "movie"
    ? { ...settings, MovieEnabled: enabled }
    : { ...settings, TvEnabled: enabled };
}

export function setLibraryEnabled(
  settings: PrefetchSettings,
  library: PlexCatalogueLibrary,
  enabled: boolean,
): LibraryMutationResult {
  if (!enabled) {
    const alreadyDisabled = settings.DisabledLibraries.some(
      (entry) =>
        entry.ServerId === library.identity.serverId &&
        entry.LibraryId === library.identity.libraryId &&
        entry.Type === library.identity.type,
    );
    return {
      settings: alreadyDisabled
        ? settings
        : {
            ...settings,
            DisabledLibraries: [
              ...settings.DisabledLibraries,
              {
                ServerId: library.identity.serverId,
                LibraryId: library.identity.libraryId,
                Type: library.identity.type,
              },
            ],
          },
      needsManualChoice: false,
      error: null,
    };
  }

  const ungated = withoutLibraryGate(settings, library.identity);
  if (settings.Sources.some((source) => sameLibrary(source, library.identity)))
    return { settings: ungated, needsManualChoice: false, error: null };

  const recommendations = library.hubs.filter((source) => {
    const recommendation = recommendationFor(source);
    return library.identity.type === "movie"
      ? recommendation === "recently-added"
      : recommendation === "on-deck" || recommendation === "continue-watching";
  });
  if (recommendations.length === 0)
    return { settings: ungated, needsManualChoice: true, error: null };
  if (ungated.Sources.length + recommendations.length > sourceLimit)
    return {
      settings,
      needsManualChoice: false,
      error: `Choose at most ${sourceLimit} Plex sources.`,
    };
  return {
    settings: {
      ...ungated,
      Sources: [
        ...ungated.Sources,
        ...recommendations.map((source) => toPrefetchSource(library, source)),
      ],
    },
    needsManualChoice: false,
    error: null,
  };
}

export function setSourceEnabled(
  settings: PrefetchSettings,
  library: PlexCatalogueLibrary,
  source: PlexSource,
  enabled: boolean,
): MutationResult {
  const key = persistedSourceKey(source.serverId, source.kind, source.key);
  const index = settings.Sources.findIndex(
    (candidate) => persistedSourceKey(candidate.ServerId, candidate.Kind, candidate.Key) === key,
  );
  if (!enabled) {
    if (index < 0) return { settings, error: null };
    return {
      settings: {
        ...settings,
        Sources: settings.Sources.map((candidate, position) =>
          position === index ? { ...candidate, Enabled: false } : candidate,
        ),
      },
      error: null,
    };
  }

  const ungated = withoutLibraryGate(settings, library.identity);
  if (index >= 0)
    return {
      settings: {
        ...ungated,
        Sources: ungated.Sources.map((candidate, position) =>
          position === index ? { ...candidate, Enabled: true } : candidate,
        ),
      },
      error: null,
    };
  if (settings.Sources.length >= sourceLimit)
    return { settings, error: `Choose at most ${sourceLimit} Plex sources.` };
  return {
    settings: {
      ...ungated,
      Sources: [...ungated.Sources, toPrefetchSource(library, source)],
    },
    error: null,
  };
}
