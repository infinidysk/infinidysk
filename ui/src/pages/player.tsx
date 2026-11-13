import { useMutation, useQuery } from "@tanstack/react-query";
import { Activity, Copy, Download, ExternalLink, FileDown, RotateCcw } from "lucide-react";
import { type ReactNode, useCallback, useEffect, useMemo, useRef, useState } from "react";
import { toast } from "sonner";
import { api, body, type Schemas } from "../api/client";
import { bytes } from "../format";
import { copyText, uuid } from "../settings";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Failure, Loading } from "../ui";
import { parse, TraceBody } from "./traces";

const AUDIO = /\.(mp3|m4a|aac|flac|ogg|oga|opus|wav)$/i;
const VIDEO = /\.(mkv|mp4|m4v|webm|mov|avi|ts|m2ts|wmv|mpg|mpeg|flv)$/i;
/** Recoveries per playback before giving up. */
const MAX_RECOVERIES = 3;
/** A wait this long while online counts as stuck and triggers a recovery. */
const STUCK_MS = 20_000;

export function playable(name: string): boolean {
  return AUDIO.test(name) || VIDEO.test(name);
}

/** Signs a `/view` link for `path`; absolute, so it can be copied out. */
export async function streamLink(path: string): Promise<string> {
  const link = await body(api.POST("/api/v1/links", { body: { path } }));
  return new URL(link.url, window.location.origin).toString();
}

export async function copyStreamLink(path: string) {
  try {
    await copyText(await streamLink(path));
    toast.success("Stream link copied; it works for 6 hours");
  } catch (e) {
    toast.error(e instanceof Error ? e.message : "Could not copy the link");
  }
}

export async function download(path: string, name: string) {
  try {
    const a = document.createElement("a");
    a.href = await streamLink(path);
    a.download = name;
    a.click();
  } catch (e) {
    toast.error(e instanceof Error ? e.message : "Could not start the download");
  }
}

type Note = { at: number; text: string };

/** Browser-side playback counters, kept in a ref and copied out once a second. */
type Stats = {
  startedAt: number;
  startupMs: number | null;
  seeks: number;
  seekStartedAt: number | null;
  lastSeekMs: number | null;
  stalls: number;
  stallStartedAt: number | null;
  stalledMs: number;
  lastProgressAt: number | null;
  lastGoodTime: number;
  recoveries: number;
  errors: Note[];
};

const freshStats = (): Stats => ({
  startedAt: performance.now(),
  startupMs: null,
  seeks: 0,
  seekStartedAt: null,
  lastSeekMs: null,
  stalls: 0,
  stallStartedAt: null,
  stalledMs: 0,
  lastProgressAt: null,
  lastGoodTime: 0,
  recoveries: 0,
  errors: [],
});

type Snapshot = Stats & {
  position: number;
  duration: number;
  bufferAhead: number;
  buffered: [number, number][];
  readyState: number;
  networkState: number;
  width: number;
  height: number;
  rate: number;
  paused: boolean;
  seeking: boolean;
  dropped: number | null;
  total: number | null;
  online: boolean;
  visible: boolean;
};

function snapshot(el: HTMLMediaElement, s: Stats): Snapshot {
  const buffered: [number, number][] = [];
  let bufferAhead = 0;
  for (let i = 0; i < el.buffered.length; i++) {
    const [start, end] = [el.buffered.start(i), el.buffered.end(i)];
    buffered.push([start, end]);
    if (start <= el.currentTime && el.currentTime <= end) bufferAhead = end - el.currentTime;
  }
  const quality = el instanceof HTMLVideoElement ? el.getVideoPlaybackQuality?.() : undefined;
  return {
    ...s,
    errors: [...s.errors],
    position: el.currentTime,
    duration: el.duration,
    bufferAhead,
    buffered,
    readyState: el.readyState,
    networkState: el.networkState,
    width: el instanceof HTMLVideoElement ? el.videoWidth : 0,
    height: el instanceof HTMLVideoElement ? el.videoHeight : 0,
    rate: el.playbackRate,
    paused: el.paused,
    seeking: el.seeking,
    dropped: quality?.droppedVideoFrames ?? null,
    total: quality?.totalVideoFrames ?? null,
    online: navigator.onLine,
    visible: document.visibilityState === "visible",
  };
}

const READY = ["nothing", "metadata", "current data", "future data", "enough data"];
const NETWORK = ["empty", "idle", "loading", "no source"];

/** Codecs every current browser decodes; others are asked via `canPlayType`. */
const SAFE_AUDIO = new Set(["aac", "mp3", "opus", "vorbis", "flac"]);
const AUDIO_MIME: Record<string, string> = {
  ac3: 'audio/mp4; codecs="ac-3"',
  eac3: 'audio/mp4; codecs="ec-3"',
  alac: 'audio/mp4; codecs="alac"',
  dts: 'audio/mp4; codecs="dtsc"',
  truehd: 'audio/mp4; codecs="mlpa"',
};
const AUDIO_NAMES: Record<string, string> = { ac3: "AC-3", eac3: "E-AC-3", dts: "DTS", truehd: "TrueHD" };

function browserDecodes(codec: string): boolean {
  if (SAFE_AUDIO.has(codec) || codec.startsWith("pcm_")) return true;
  const mime = AUDIO_MIME[codec];
  return mime != null && document.createElement("audio").canPlayType(mime) !== "";
}

/** A warning when the track the browser plays (the default, else the first) has audio it cannot decode. */
function audioWarning(info: Schemas["MediaInfo"] | null | undefined): string | null {
  const tracks = info?.streams.filter((s) => s.kind === "audio") ?? [];
  const track = tracks.find((s) => s.default) ?? tracks[0];
  if (!track || browserDecodes(track.codec)) return null;
  const codec = [AUDIO_NAMES[track.codec] ?? track.codec, track.channel_layout].filter(Boolean).join(" ");
  return `The audio is ${codec}, which this browser cannot decode; the video plays without sound.`;
}

function clock(seconds: number): string {
  if (!Number.isFinite(seconds)) return "–";
  const s = Math.floor(seconds);
  const h = Math.floor(s / 3600);
  const mm = String(Math.floor((s % 3600) / 60)).padStart(h ? 2 : 1, "0");
  return `${h ? `${h}:` : ""}${mm}:${String(s % 60).padStart(2, "0")}`;
}

function rate(bytesPerSecond: number): string {
  return `${bytes(bytesPerSecond)}/s`;
}

export function Player({ entry, onClose }: { entry: Schemas["FileEntry"]; onClose: () => void }) {
  const player = useMemo(uuid, []);
  const audio = AUDIO.test(entry.name);
  const link = useQuery({
    queryKey: ["link", entry.path],
    queryFn: () => streamLink(entry.path),
    staleTime: 60 * 60 * 1000,
  });
  const src = link.data ? `${link.data}&player=${player}` : undefined;
  const id = entry.item_id;
  const details = useQuery({
    queryKey: ["file", id],
    queryFn: () => body(api.GET("/api/v1/files/{id}", { params: { path: { id: id! } } })),
    enabled: id != null,
  });
  const silent = audioWarning(details.data?.media?.info);
  const media = useRef<HTMLVideoElement & HTMLAudioElement>(null);
  const stats = useRef<Stats>(freshStats());
  const [snap, setSnap] = useState<Snapshot | null>(null);
  const [unsupported, setUnsupported] = useState<string | null>(null);
  const [failed, setFailed] = useState<string | null>(null);
  const [diagnostics, setDiagnostics] = useState(false);

  const note = useCallback((text: string) => {
    stats.current.errors = [...stats.current.errors.slice(-19), { at: Date.now(), text }];
  }, []);

  /** Reloads the source and resumes where playback last made progress. */
  const recover = useCallback(
    (why: string, force = false) => {
      const el = media.current;
      const s = stats.current;
      if (!el || !src) return;
      if (!force && s.recoveries >= MAX_RECOVERIES) {
        setFailed(`Playback failed: ${why}. Gave up after ${MAX_RECOVERIES} attempts.`);
        return;
      }
      if (!force) s.recoveries++;
      note(`${force ? "reload" : "recovery"}: ${why}`);
      const resume = s.lastGoodTime;
      s.stallStartedAt = null;
      el.src = `${src}&r=${Date.now()}`;
      el.load();
      el.addEventListener(
        "loadedmetadata",
        () => {
          el.currentTime = resume;
          void el.play().catch(() => undefined);
        },
        { once: true },
      );
    },
    [src, note],
  );

  useEffect(() => {
    const el = media.current;
    if (!el || !src) return;
    const s = stats.current;
    const now = () => performance.now();
    const handlers: Record<string, () => void> = {
      canplay: () => {
        if (s.startupMs == null) s.startupMs = now() - s.startedAt;
      },
      loadedmetadata: () => {
        if (!audio && el.videoWidth === 0 && el.videoHeight === 0)
          setUnsupported("The browser found no video track it can decode; audio may still play.");
      },
      seeking: () => {
        s.seeks++;
        s.seekStartedAt = now();
      },
      seeked: () => {
        if (s.seekStartedAt != null) s.lastSeekMs = now() - s.seekStartedAt;
        s.seekStartedAt = null;
      },
      waiting: () => {
        if (s.stallStartedAt == null && s.startupMs != null) {
          s.stalls++;
          s.stallStartedAt = now();
        }
      },
      playing: () => {
        if (s.stallStartedAt != null) s.stalledMs += now() - s.stallStartedAt;
        s.stallStartedAt = null;
      },
      timeupdate: () => {
        if (!el.paused && !el.seeking && el.readyState >= 3) {
          s.lastGoodTime = el.currentTime;
          s.lastProgressAt = Date.now();
        }
      },
      error: () => {
        const err = el.error;
        const message = err?.message || `media error ${err?.code ?? "?"}`;
        note(message);
        if (err?.code === MediaError.MEDIA_ERR_SRC_NOT_SUPPORTED && s.startupMs == null) {
          setUnsupported(`The browser cannot play this format (${message}).`);
        } else if (err?.code === MediaError.MEDIA_ERR_SRC_NOT_SUPPORTED || err?.code === MediaError.MEDIA_ERR_DECODE) {
          setFailed(`The browser stopped decoding this file (${message}).`);
        } else {
          recover(message);
        }
      },
    };
    for (const [name, fn] of Object.entries(handlers)) el.addEventListener(name, fn);
    const tick = window.setInterval(() => {
      if (s.stallStartedAt != null && navigator.onLine && performance.now() - s.stallStartedAt > STUCK_MS) {
        s.stalledMs += performance.now() - s.stallStartedAt;
        recover(`no data for ${STUCK_MS / 1000} s`);
      }
      setSnap(snapshot(el, s));
    }, 1000);
    return () => {
      for (const [name, fn] of Object.entries(handlers)) el.removeEventListener(name, fn);
      window.clearInterval(tick);
    };
  }, [src, audio, recover, note]);

  const name = entry.name;
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent className="max-h-[92vh] grid-cols-[minmax(0,1fr)] overflow-y-auto sm:max-w-5xl">
        <DialogHeader>
          <DialogTitle className="truncate pr-6" title={name}>
            {name}
          </DialogTitle>
          <DialogDescription className="truncate font-mono text-xs" title={entry.path}>
            {entry.path}
          </DialogDescription>
        </DialogHeader>
        {link.isPending ? (
          <Loading />
        ) : link.isError ? (
          <Failure error={link.error} />
        ) : (
          <>
            {audio ? (
              <audio ref={media} src={src} controls autoPlay className="w-full" />
            ) : (
              <video
                ref={media}
                src={src}
                controls
                autoPlay
                playsInline
                className="aspect-video max-h-[calc(92vh-12rem)] w-full rounded-md bg-black"
              />
            )}
            {(unsupported || failed || silent) && (
              <div className="border-warning/40 bg-warning/10 rounded-md border px-3 py-2">
                <p>{unsupported ?? failed ?? silent}</p>
                <p className="text-muted-foreground mt-0.5 text-xs">
                  Download it, or copy the stream link into a player such as mpv or VLC.
                </p>
              </div>
            )}
            <div className="flex flex-wrap items-center gap-2">
              <Button variant={diagnostics ? "secondary" : "outline"} size="sm" onClick={() => setDiagnostics((d) => !d)}>
                <Activity />
                Diagnostics
              </Button>
              <Button variant="outline" size="sm" asChild>
                <a href={link.data} target="_blank" rel="noreferrer">
                  <ExternalLink />
                  Open direct
                </a>
              </Button>
              <Button variant="outline" size="sm" asChild>
                <a href={link.data} download={name}>
                  <Download />
                  Download
                </a>
              </Button>
              <Button variant="outline" size="sm" onClick={() => void copyStreamLink(entry.path)}>
                <Copy />
                Copy link
              </Button>
              <Button variant="ghost" size="sm" onClick={() => recover("manual", true)} title="Reload and resume where it left off">
                <RotateCcw />
                Reload
              </Button>
              {entry.size != null && <span className="text-muted-foreground ml-auto tabular-nums">{bytes(entry.size)}</span>}
            </div>
            {diagnostics && snap && (
              <Diagnostics player={player} snap={snap} reload={() => recover("tracing", true)} fileName={name} />
            )}
          </>
        )}
      </DialogContent>
    </Dialog>
  );
}

function Row({ label, children }: { label: string; children: ReactNode }) {
  return (
    <>
      <dt className="text-muted-foreground">{label}</dt>
      <dd className="truncate text-right tabular-nums">{children}</dd>
    </>
  );
}

function Section({ title, children, aside }: { title: string; children: ReactNode; aside?: ReactNode }) {
  return (
    <section className="rounded-md border p-3">
      <div className="mb-2 flex items-center justify-between">
        <h3 className="text-muted-foreground text-xs font-medium tracking-wide uppercase">{title}</h3>
        {aside}
      </div>
      <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-xs">{children}</dl>
    </section>
  );
}

type Served = { at: number; served: number };

function Diagnostics({
  player,
  snap,
  reload,
  fileName,
}: {
  player: string;
  snap: Snapshot;
  reload: () => void;
  fileName: string;
}) {
  // Every read this player opened stays here after it closes, for totals.
  const seen = useRef(new Map<number, Schemas["ReadView"]>());
  const samples = useRef<Served[]>([]);
  const reads = useQuery({
    queryKey: ["reads", player],
    queryFn: async () => {
      const open = await body(api.GET("/api/v1/reads", { params: { query: { player } } }));
      for (const r of open) seen.current.set(r.id, r);
      return open;
    },
    refetchInterval: 1000,
  });
  const all = [...seen.current.values()];
  const current = reads.data?.at(-1) ?? all.at(-1);
  const served = all.reduce((sum, r) => sum + r.served, 0);
  const now = Date.now();
  const at = reads.dataUpdatedAt;
  if (reads.isSuccess && samples.current.at(-1)?.at !== at)
    samples.current = [...samples.current.filter((s) => at - s.at < 5000), { at, served }];
  const [first, last] = [samples.current[0], samples.current.at(-1)];
  const throughput = first && last && last.at > first.at ? ((last.served - first.served) * 1000) / (last.at - first.at) : 0;
  const stalls = all.reduce((sum, r) => sum + r.stalls, 0);
  const stallMs = all.reduce((sum, r) => sum + r.stall_ms, 0);

  const traced = all.filter((r) => r.traced).at(-1);
  const trace = useQuery({
    queryKey: ["trace", traced?.id],
    enabled: traced != null,
    queryFn: async () =>
      parse(await body(api.GET("/api/v1/traces/{id}", { params: { path: { id: traced!.id } }, parseAs: "text" }))),
    refetchInterval: traced?.closed_at == null ? 3000 : false,
  });
  const startTrace = useMutation({
    mutationFn: () => body(api.PUT("/api/v1/tracing", { body: { minutes: 15 } })),
    onSuccess: reload,
  });

  const exportBundle = async () => {
    const ext = fileName.includes(".") ? fileName.slice(fileName.lastIndexOf(".")) : "";
    let traceExport: string | null = null;
    if (traced) {
      const res = await fetch(`/api/v1/traces/${traced.id}/export`);
      if (res.ok) traceExport = await res.text();
    }
    const bundle = {
      file: `<file>${ext}`,
      userAgent: navigator.userAgent,
      browser: snap,
      reads: all.map((r) => ({ ...r, path: `<file>${ext}`, client: "<redacted>" })),
      trace: traceExport,
    };
    const a = document.createElement("a");
    a.href = URL.createObjectURL(new Blob([JSON.stringify(bundle, null, 2)], { type: "application/json" }));
    a.download = `player-${player.slice(0, 8)}.json`;
    a.click();
    URL.revokeObjectURL(a.href);
  };

  const s = snap;
  return (
    <div className="space-y-3">
      <div className="grid gap-3 md:grid-cols-2">
        <Section title="Player">
          <Row label="Status">{s.paused ? "Paused" : s.seeking ? "Seeking" : s.stallStartedAt != null ? "Waiting" : "Playing"}</Row>
          <Row label="Startup">{s.startupMs == null ? "–" : `${Math.round(s.startupMs)} ms`}</Row>
          <Row label="Position">
            {clock(s.position)} / {clock(s.duration)}
          </Row>
          <Row label="Buffer ahead">{s.bufferAhead.toFixed(1)} s</Row>
          <Row label="Buffered">{s.buffered.map(([a, b]) => `${clock(a)}–${clock(b)}`).join(", ") || "–"}</Row>
          <Row label="Ready state">{READY[s.readyState] ?? s.readyState}</Row>
          <Row label="Network state">{NETWORK[s.networkState] ?? s.networkState}</Row>
          {s.width > 0 && (
            <Row label="Resolution">
              {s.width}×{s.height}
            </Row>
          )}
          <Row label="Playback rate">{s.rate}×</Row>
          <Row label="Seeks">
            {s.seeks}
            {s.lastSeekMs != null && ` · last ${Math.round(s.lastSeekMs)} ms`}
          </Row>
          <Row label="Stalls">
            {s.stalls} · {(s.stalledMs / 1000).toFixed(1)} s
          </Row>
          {s.dropped != null && (
            <Row label="Dropped frames">
              {s.dropped} / {s.total}
            </Row>
          )}
          <Row label="Last progress">{s.lastProgressAt ? `${Math.round((now - s.lastProgressAt) / 1000)} s ago` : "–"}</Row>
          <Row label="Recoveries">
            {s.recoveries} / {MAX_RECOVERIES}
          </Row>
          <Row label="Online · visible">
            {s.online ? "yes" : "no"} · {s.visible ? "yes" : "no"}
          </Row>
        </Section>
        <Section
          title="Server read"
          aside={
            <Button
              variant="ghost"
              size="xs"
              title="Copy player session id"
              onClick={() =>
                void copyText(player)
                  .then(() => toast.success("Session id copied"))
                  .catch((e: Error) => toast.error(e.message))
              }
            >
              <Copy />
              {player.slice(0, 8)}
            </Button>
          }
        >
          {reads.isError ? (
            <Row label="Error">{reads.error.message}</Row>
          ) : !current ? (
            <Row label="Read">none open</Row>
          ) : (
            <>
              <Row label="Read">
                #{current.id}
                {current.closed_at != null && " (closed)"}
              </Row>
              <Row label="Offset">
                {bytes(current.position)} / {bytes(current.size)}
              </Row>
              <Row label="Served">{bytes(served)}</Row>
              <Row label="Rate">{rate(throughput)}</Row>
              <Row label="Stream first byte">{current.first_byte_ms == null ? "–" : `${current.first_byte_ms} ms`}</Row>
              <Row label="Stalls">
                {stalls} · {(stallMs / 1000).toFixed(1)} s
              </Row>
              <Row label="Seeks">{all.reduce((sum, r) => sum + r.seeks, 0)}</Row>
              <Row label="Reads opened">{all.length}</Row>
              <Row label="Traced">{traced ? `#${traced.id}` : "no"}</Row>
            </>
          )}
        </Section>
      </div>
      {s.errors.length > 0 && (
        <section className="rounded-md border p-3">
          <h3 className="text-muted-foreground mb-2 text-xs font-medium tracking-wide uppercase">Events</h3>
          <ul className="space-y-1 text-xs">
            {s.errors.map((e) => (
              <li key={e.at + e.text} className="flex gap-3">
                <span className="text-muted-foreground shrink-0 tabular-nums">{new Date(e.at).toLocaleTimeString()}</span>
                <span className="min-w-0 break-words">{e.text}</span>
              </li>
            ))}
          </ul>
        </section>
      )}
      <div className="flex flex-wrap items-center gap-2">
        <Button
          variant="outline"
          size="sm"
          disabled={startTrace.isPending}
          onClick={() => startTrace.mutate()}
          title="Turns on read tracing for 15 minutes and reloads, so this playback is traced"
        >
          <Activity />
          {traced ? "Trace again" : "Trace this playback"}
        </Button>
        <Button variant="outline" size="sm" onClick={() => void exportBundle()} title="File and provider names redacted">
          <FileDown />
          Export bundle
        </Button>
        {startTrace.isError && <span className="text-destructive text-xs">{startTrace.error.message}</span>}
      </div>
      {traced && (
        <div className={cn("rounded-md border p-3", trace.isFetching && "opacity-90")}>
          {trace.isPending ? <Loading /> : trace.isError ? <Failure error={trace.error} /> : <TraceBody {...trace.data} />}
        </div>
      )}
    </div>
  );
}
