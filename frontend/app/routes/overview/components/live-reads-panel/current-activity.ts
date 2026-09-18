export type PlaybackSourceType = "Plex" | "Emby" | "Jellyfin" | "InfiniDysk";
export type PlaybackState = "Unknown" | "Playing" | "Paused" | "Buffering";
export type PlaybackDeliveryMethod = "Unknown" | "DirectPlay" | "DirectStream" | "Transcode";
export type PlaybackFreshness = "Fresh" | "Stale";

export type PlaybackSessionKey = {
  sourceInstanceId: string;
  nativeSessionId: string;
};

export type AuthoritativePlaybackSession = {
  key: PlaybackSessionKey;
  sourceInstanceName: string;
  sourceType: PlaybackSourceType;
  nativeSessionId: string;
  userName: string | null;
  clientName: string | null;
  deviceName: string | null;
  itemId: string | null;
  title: string | null;
  mediaType: string | null;
  seriesName: string | null;
  seasonNumber: number | null;
  episodeNumber: number | null;
  state: PlaybackState;
  positionMs: number | null;
  durationMs: number | null;
  deliveryMethod: PlaybackDeliveryMethod;
  mediaSourceId: string | null;
  mediaSourcePath: string | null;
  davItemId: string;
  lastConfirmedAt: string;
  freshness: PlaybackFreshness;
};

export type CurrentPlaybackActivity = {
  session: AuthoritativePlaybackSession;
  transportReadIds: string[];
  hasSharedFileTransport: boolean;
};

export type CurrentProviderContribution = {
  host: string;
  nickname: string | null;
  segments: number;
};

export type CurrentTransportActivity = {
  id: string;
  davItemId: string | null;
  fileName: string;
  path: string;
  startedAt: string;
  lastActivityAt: string;
  bytesRead: number;
  bytesFetched: number;
  sourceOffset: number;
  fileSize: number | null;
  clientIp: string | null;
  clientUserAgent: string | null;
  playerSession: string | null;
  correlationScope: number | "None" | "Session" | "File";
  matchingPlaybackSessionCount: number;
  shared: boolean;
  providers: CurrentProviderContribution[];
};

export type PlaybackAuthoritySnapshot = {
  sourceInstanceId: string;
  sourceInstanceName: string;
  sourceType: PlaybackSourceType;
  available: boolean;
  isStale: boolean;
  lastSuccessfulPollAt: string | null;
  lastFailureAt: string | null;
  lastErrorKind: string | null;
};

export type CurrentActivitySnapshot = {
  playback: CurrentPlaybackActivity[];
  reads: CurrentTransportActivity[];
  authorities: PlaybackAuthoritySnapshot[];
};
