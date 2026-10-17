import { useCallback, useEffect, useRef } from "react";
import { withUrlBase } from "~/utils/url-base";

export type NativePlaybackState = "Playing" | "Paused" | "Buffering";

export type NativePlaybackActivity = {
  state: NativePlaybackState;
  positionSeconds: number | null;
  durationSeconds: number | null;
};

const PROGRESS_REPORT_INTERVAL_MS = 5_000;
const HEARTBEAT_INTERVAL_MS = 25_000;

type NativePlaybackReporterOptions = {
  playerSession: string;
  title: string;
  mediaType: "video" | "audio";
};

/**
 * Reports the Explore player's own authoritative state to the backend.
 *
 * Reports are deliberately independent of WebDAV traffic: transport only
 * establishes the exact DavItem association. Once established, heartbeats
 * keep a playing/paused session alive while rclone or browser buffering makes
 * the InfiniDysk source read go idle.
 */
export function useNativePlaybackReporter({
  playerSession,
  title,
  mediaType,
}: NativePlaybackReporterOptions) {
  const latestRef = useRef<NativePlaybackActivity | null>(null);
  const lastSentAtRef = useRef(0);
  const lastSentStateRef = useRef<NativePlaybackState | null>(null);
  const endedRef = useRef(false);
  const pendingEndRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  const post = useCallback(
    (activity: NativePlaybackActivity | null, end: boolean, keepalive = false) => {
      const body = end
        ? { playerSession, event: "End" }
        : {
            playerSession,
            event: "Report",
            state: activity!.state,
            positionMs: secondsToMilliseconds(activity!.positionSeconds),
            durationMs: secondsToMilliseconds(activity!.durationSeconds),
            title,
            mediaType,
          };

      void fetch(withUrlBase("/api/playback/native"), {
        method: "POST",
        headers: {
          Accept: "application/json",
          "Content-Type": "application/json",
        },
        body: JSON.stringify(body),
        keepalive,
      }).catch(() => {
        // Player reporting is observability, never a playback dependency.
        // A 409 while the first /view request is still establishing exact
        // identity is also expected; the next event/heartbeat retries it.
      });
    },
    [mediaType, playerSession, title],
  );

  const report = useCallback(
    (activity: NativePlaybackActivity, force = false) => {
      latestRef.current = activity;
      endedRef.current = false;

      const now = Date.now();
      const stateChanged = lastSentStateRef.current !== activity.state;
      if (!force && !stateChanged && now - lastSentAtRef.current < PROGRESS_REPORT_INTERVAL_MS) {
        return;
      }

      lastSentAtRef.current = now;
      lastSentStateRef.current = activity.state;
      post(activity, false);
    },
    [post],
  );

  const end = useCallback(
    (keepalive = false) => {
      if (endedRef.current) return;
      endedRef.current = true;
      latestRef.current = null;
      lastSentStateRef.current = null;
      post(null, true, keepalive);
    },
    [post],
  );

  useEffect(() => {
    if (pendingEndRef.current !== null) {
      clearTimeout(pendingEndRef.current);
      pendingEndRef.current = null;
    }

    const heartbeat = setInterval(() => {
      const latest = latestRef.current;
      if (latest === null || endedRef.current) return;
      lastSentAtRef.current = Date.now();
      post(latest, false);
    }, HEARTBEAT_INTERVAL_MS);

    const onPageHide = () => end(true);
    globalThis.addEventListener?.("pagehide", onPageHide);

    return () => {
      clearInterval(heartbeat);
      globalThis.removeEventListener?.("pagehide", onPageHide);

      // React StrictMode may run an effect cleanup/setup pair without a real
      // unmount. Defer End one task so an immediate setup can cancel it.
      pendingEndRef.current = setTimeout(() => {
        pendingEndRef.current = null;
        end(true);
      }, 0);
    };
  }, [end, post]);

  return { report, end };
}

function secondsToMilliseconds(value: number | null): number | null {
  if (value === null || !Number.isFinite(value) || value < 0) return null;
  return Math.round(value * 1000);
}
