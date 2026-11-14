import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  ArrowDownNarrowWide,
  ArrowUpNarrowWide,
  ChevronDown,
  ChevronRight,
  Copy,
  Download,
  File,
  FileArchive,
  Folder,
  Info,
  Link2,
  Loader2,
  Play,
  RefreshCw,
  ScanSearch,
  Search,
  SlidersHorizontal,
  Stethoscope,
  Trash2,
  Wrench,
  X,
} from "lucide-react";
import { type CSSProperties, type ReactNode, useEffect, useRef, useState } from "react";
import { Link, useSearchParams } from "react-router";
import { toast } from "sonner";
import { api, body, type Schemas } from "../api/client";
import type { operations } from "../api/schema";
import { ago, bytes, duration, stamp } from "../format";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Switch } from "@/components/ui/switch";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import {
  CheckStatus,
  checkRepairs,
  ConfirmButton,
  Empty,
  Failure,
  healthStates,
  IconButton,
  InfoTip,
  Loading,
  PAGE,
  PageHeader,
  Pager,
  Panel,
  ReleaseName,
  RowActions,
  Status,
} from "../ui";
import { copyStreamLink, download, playable, Player } from "./player";

type Entry = Schemas["FileEntry"];
type Filters = NonNullable<operations["get_files"]["parameters"]["query"]>;

/** Tree page size: one Load more adds this many children. */
const LIMIT = 100;
/** Select values cannot be empty; this stands in for "no filter". */
const ANY = "any";
const DAY = 24 * 60 * 60 * 1000;
const WITHIN: Record<string, number> = { "24h": DAY, "7d": 7 * DAY, "30d": 30 * DAY };

async function files(query: Filters) {
  return body(api.GET("/api/v1/files", { params: { query } }));
}

function HealthStatus({ entry, compact }: { entry: Entry; compact?: boolean }) {
  if (entry.item_id == null || entry.kind === "directory" || !entry.state) return null;
  const [label, tone] = healthStates[entry.state];
  return (
    <Status tone={tone} compact={compact}>
      {label}
    </Status>
  );
}

function Icon({ entry }: { entry: Entry }) {
  const className = "text-muted-foreground size-4 shrink-0";
  if (entry.kind === "directory") return <Folder className={className} />;
  if (entry.kind === "symlink") return <Link2 className={className} />;
  if (entry.storage === "multipart") return <FileArchive className={className} />;
  return <File className={className} />;
}

type Actions = {
  play: (entry: Entry) => void;
  select: (entry: Entry) => void;
  check: (id: string) => void;
};

function EntryActions({ entry, actions }: { entry: Entry; actions: Actions }) {
  const file = entry.kind === "file";
  const id = entry.item_id;
  return (
    <RowActions
      name={entry.name}
      actions={[
        file && playable(entry.name) && { label: "Play", icon: <Play />, onSelect: () => actions.play(entry) },
        !!id && entry.path !== "/content" && { label: "Details", icon: <Info />, onSelect: () => actions.select(entry) },
        file && { label: "Download", icon: <Download />, onSelect: () => void download(entry.path, entry.name) },
        !!id && file && { label: "Re-check health", icon: <Stethoscope />, onSelect: () => actions.check(id) },
      ]}
    />
  );
}

function Cells({ entry, actions, name }: { entry: Entry; actions: Actions; name: ReactNode }) {
  return (
    <>
      <TableCell className="w-full max-w-0">{name}</TableCell>
      <TableCell>
        {entry.kind === "directory" || !entry.state ? (
          <span className="text-muted-foreground hidden sm:inline">–</span>
        ) : (
          <HealthStatus entry={entry} compact />
        )}
      </TableCell>
      <TableCell className="text-muted-foreground hidden lg:table-cell" title={entry.next_check ? stamp(entry.next_check) : undefined}>
        {entry.next_check ? ago(entry.next_check) : ""}
      </TableCell>
      <TableCell className="text-muted-foreground hidden md:table-cell" title={entry.added ? stamp(entry.added) : undefined}>
        {entry.added ? ago(entry.added) : ""}
      </TableCell>
      <TableCell className="hidden text-right tabular-nums sm:table-cell">
        {entry.size != null ? bytes(entry.size) : <span className="text-muted-foreground">–</span>}
      </TableCell>
      <TableCell>
        <EntryActions entry={entry} actions={actions} />
      </TableCell>
    </>
  );
}

function Name({ entry, detail }: { entry: Entry; detail?: ReactNode }) {
  return (
    <span className="flex min-w-0 items-center gap-2">
      <Icon entry={entry} />
      <span className="min-w-0">
        <span className="block truncate" title={entry.name}>
          <ReleaseName name={entry.name} />
        </span>
        {detail}
      </span>
    </span>
  );
}

// Tree indents scale with `--depth`, tighter on phones so names keep their room.
const indent = "pl-[calc(var(--depth)*0.75rem)] sm:pl-[calc(var(--depth)*1.25rem)]";
const inset = "pl-[calc(var(--depth)*0.75rem+1.75rem)] sm:pl-[calc(var(--depth)*1.25rem+1.75rem)]";

/** One directory's children in tree mode, each expandable in place. */
function TreeRows({
  path,
  depth,
  sort,
  actions,
  selected,
  system = true,
}: {
  path: string;
  depth: number;
  sort: Pick<Filters, "sort" | "order">;
  actions: Actions;
  selected?: string;
  /** At the root, false shows only `/content`. */
  system?: boolean;
}) {
  const [limit, setLimit] = useState(LIMIT);
  const [open, setOpen] = useState<Set<string>>(() => new Set(depth === 0 ? ["/content"] : []));
  const page = useQuery({
    queryKey: ["files", { path, ...sort, limit }],
    queryFn: () => files({ path, ...sort, limit }),
    placeholderData: keepPreviousData,
  });
  const depthVar = { "--depth": depth } as CSSProperties;
  if (page.isPending || page.isError)
    return (
      <TableRow>
        <TableCell colSpan={6} style={depthVar} className={cn(indent, page.isError ? "text-destructive" : "text-muted-foreground")}>
          {page.isError ? page.error.message : "Loading…"}
        </TableCell>
      </TableRow>
    );
  const toggle = (p: string) =>
    setOpen((o) => {
      const next = new Set(o);
      if (!next.delete(p)) next.add(p);
      return next;
    });
  const entries = system ? page.data.entries : page.data.entries.filter((e) => e.path === "/content");
  return (
    <>
      {entries.map((entry) => {
        const dir = entry.kind === "directory";
        const expanded = open.has(entry.path);
        return [
          <TableRow key={entry.path} data-state={selected && entry.item_id === selected ? "selected" : undefined}>
            <Cells
              entry={entry}
              actions={actions}
              name={
                <button
                  type="button"
                  style={depthVar}
                  className={cn(indent, "flex w-full min-w-0 items-center gap-1 text-left")}
                  onClick={() => (dir ? toggle(entry.path) : entry.item_id && actions.select(entry))}
                >
                  {dir ? (
                    expanded ? (
                      <ChevronDown className="size-4 shrink-0" />
                    ) : (
                      <ChevronRight className="size-4 shrink-0" />
                    )
                  ) : (
                    <span className="w-4 shrink-0" />
                  )}
                  <Name entry={entry} />
                </button>
              }
            />
          </TableRow>,
          dir && expanded && (
            <TreeRows
              key={`${entry.path}/`}
              path={entry.path}
              depth={depth + 1}
              sort={sort}
              actions={actions}
              selected={selected}
            />
          ),
        ];
      })}
      {page.data.total > page.data.entries.length && (
        <TableRow>
          <TableCell colSpan={6} style={depthVar} className={inset}>
            <Button variant="ghost" size="xs" onClick={() => setLimit((l) => l + LIMIT)} disabled={page.isFetching}>
              Load {Math.min(LIMIT, page.data.total - page.data.entries.length)} more of{" "}
              {page.data.total - page.data.entries.length}
            </Button>
          </TableCell>
        </TableRow>
      )}
      {page.data.total === 0 && (
        <TableRow>
          <TableCell colSpan={6} style={depthVar} className={cn(inset, "text-muted-foreground")}>
            Empty
          </TableCell>
        </TableRow>
      )}
    </>
  );
}

function Fact({ label, children }: { label: string; children: ReactNode }) {
  return (
    <>
      <dt className="text-muted-foreground">{label}</dt>
      <dd className="min-w-0 break-words">{children}</dd>
    </>
  );
}

const outcomes: Record<Schemas["AnalysisOutcome"], string> = {
  discovered: "Analyzed",
  no_streams: "No audio or video found",
  unsupported: "Not a format analysis reads",
  inconclusive: "Analysis failed",
};

/** One stream as a compact line: "Video · hevc Main 10 · 3840×2160 · 23.976 fps · HDR (PQ)". */
function streamLine(s: Schemas["MediaStream"]): string {
  const parts = [
    s.kind.charAt(0).toUpperCase() + s.kind.slice(1),
    [s.codec, s.profile].filter(Boolean).join(" "),
    s.width && s.height ? `${s.width}×${s.height}` : null,
    s.frame_rate ? `${Number(s.frame_rate.toFixed(3))} fps` : null,
    s.hdr ? `HDR (${s.hdr.toUpperCase()})` : null,
    s.channel_layout ?? (s.channels ? `${s.channels} ch` : null),
    s.sample_rate ? `${s.sample_rate / 1000} kHz` : null,
    s.bps ? `${bytes(s.bps)}/s` : null,
    s.language,
    s.title,
    s.default ? "default" : null,
    s.forced ? "forced" : null,
  ];
  return parts.filter(Boolean).join(" · ");
}

/** All subtitle streams as one line: "3 subtitles · subrip · eng, spa (Latin American), fre forced". */
function subtitleLine(subs: Schemas["MediaStream"][]): string {
  const codecs = [...new Set(subs.map((s) => s.codec))].join(", ");
  const tracks = subs.map((s) =>
    [s.language ?? "und", s.title && `(${s.title})`, s.forced && "forced"].filter(Boolean).join(" "),
  );
  return [`${subs.length} subtitle${subs.length === 1 ? "" : "s"}`, codecs, tracks.join(", ")].join(" · ");
}

function Media({ media }: { media: Schemas["MediaView"] }) {
  const { info, defect } = media;
  const subs = info?.streams.filter((s) => s.kind === "subtitle") ?? [];
  return (
    <div>
      <h3 className="text-muted-foreground mb-1.5 flex items-center gap-1.5 text-xs font-medium tracking-wide uppercase">
        Media
        <InfoTip label="About media analysis">Analyze reads the container with ffprobe and lists its streams.</InfoTip>
      </h3>
      {defect && (
        <p className="text-destructive mb-1.5">
          Broken container: the box at byte {defect.offset_bytes.toLocaleString()} claims {bytes(defect.claimed_bytes)}.
        </p>
      )}
      {info ? (
        <>
          <p>
            {info.container}
            {info.duration_ms != null && ` · ${duration(Math.round(info.duration_ms / 1000))}`}
            {info.bps != null && ` · ${bytes(info.bps)}/s`}
          </p>
          <ul className="mt-1 space-y-0.5">
            {info.streams
              .filter((s) => s.kind !== "subtitle")
              .map((s, i) => (
                <li key={i} className="text-muted-foreground text-xs">
                  {streamLine(s)}
                </li>
              ))}
            {subs.length > 0 && <li className="text-muted-foreground text-xs">{subtitleLine(subs)}</li>}
          </ul>
        </>
      ) : (
        !media.pending &&
        !media.outcome && (
          <p className="text-muted-foreground">
            {media.available ? "Not analyzed yet." : "Install ffprobe to analyze media."}
          </p>
        )
      )}
      {media.pending ? (
        <p className="text-muted-foreground mt-1 text-xs">Analysis queued; it runs while nothing is streaming or importing.</p>
      ) : (
        media.outcome &&
        media.outcome !== "discovered" && (
          <p className="text-muted-foreground mt-1 text-xs" title={media.attempted_at ? stamp(media.attempted_at) : undefined}>
            {outcomes[media.outcome]}
            {media.reason && `: ${media.reason}`}
            {media.attempted_at && ` (${ago(media.attempted_at)})`}
          </p>
        )
      )}
    </div>
  );
}

// Cached and local reads round down to 0 ms.
const firstByte = (ms: number) => (ms < 1 ? "<1 ms" : `${ms} ms`);
const stallCount = (n: number) => `${n} ${n === 1 ? "stall" : "stalls"}`;

function ReadHistory({ id }: { id: string }) {
  const reads = useQuery({
    queryKey: ["file-reads", id],
    queryFn: () => body(api.GET("/api/v1/files/{id}/reads", { params: { path: { id } } })),
  });
  const list = reads.data ?? [];
  const served = list.reduce((n, r) => n + r.served, 0);
  const stalls = list.reduce((n, r) => n + r.stalls, 0);
  const waits = list.flatMap((r) => (r.first_byte_ms == null ? [] : [r.first_byte_ms])).sort((a, b) => a - b);
  return (
    <div>
      <h3 className="text-muted-foreground mb-1.5 text-xs font-medium tracking-wide uppercase">Reads</h3>
      {reads.isPending ? (
        <p className="text-muted-foreground">Loading…</p>
      ) : reads.isError ? (
        <p className="text-destructive">{reads.error.message}</p>
      ) : list.length === 0 ? (
        <p className="text-muted-foreground">Not read in the last 90 days.</p>
      ) : (
        <>
          <p>
            {list.length} {list.length === 1 ? "open" : "opens"} · {bytes(served)} read
            {waits.length > 0 && ` · stream first byte ${firstByte(waits[Math.floor(waits.length / 2)]!)} median`}
            {stalls > 0 && <span className="text-warning-text"> · {stallCount(stalls)}</span>}
          </p>
          <ul className="mt-1 space-y-0.5">
            {list.slice(0, 8).map((r) => (
              <li key={`${r.opened_at}-${r.closed_at}`} className="text-muted-foreground flex items-baseline gap-3 text-xs">
                <span className="w-28 shrink-0" title={stamp(r.closed_at)}>
                  {ago(r.closed_at)}
                </span>
                <span className="min-w-0 truncate" title={r.client}>
                  {r.delivery === "fuse" ? "FUSE" : "WebDAV"} · {bytes(r.served)}
                  {r.first_byte_ms != null && ` · ${firstByte(r.first_byte_ms)}`}
                  {r.stalls > 0 && ` · ${stallCount(r.stalls)}`}
                  {r.end_reason === "eof" && " · to the end"}
                </span>
              </li>
            ))}
          </ul>
        </>
      )}
    </div>
  );
}

function Remove({ id, onRemoved }: { id: string; onRemoved: () => void }) {
  const [open, setOpen] = useState(false);
  const [forget, setForget] = useState(false);
  const preview = useQuery({
    queryKey: ["removal", id],
    queryFn: () => body(api.GET("/api/v1/files/{id}/removal", { params: { path: { id } } })),
    enabled: open,
  });
  const remove = useMutation({
    mutationFn: () => body(api.DELETE("/api/v1/files/{id}", { params: { path: { id }, query: { forget_history: forget } } })),
    onSuccess: () => {
      toast.success("Removed");
      setOpen(false);
      onRemoved();
    },
  });
  const p = preview.data;
  const deletable = !!p && !p.kept;
  return (
    <>
      <Button variant="outline" size="sm" onClick={() => setOpen(true)}>
        <Trash2 />
        Remove
      </Button>
      <Dialog
        open={open}
        onOpenChange={(next) => {
          setOpen(next);
          if (!next) remove.reset();
        }}
      >
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Remove from the library?</DialogTitle>
            <DialogDescription>
              {preview.isPending
                ? "Counting what this removes…"
                : preview.isError
                  ? "Could not count what this removes."
                  : p!.kept === "configured"
                    ? <>
                        This category is still offered to download clients, so the folder would come back. Remove it
                        under{" "}
                        <Link to="/settings/download-client" className="text-foreground relative underline underline-offset-4 after:absolute after:-inset-y-1 after:inset-x-0">
                          Download client
                        </Link>{" "}
                        first.
                      </>
                    : p!.kept === "not_empty"
                      ? "A category folder can be removed once no files are left in it. Remove the items inside it first."
                      : p!.kept === "root"
                        ? "This folder is part of the library layout and stays."
                        : p!.files === 0
                          ? "There are no files in this folder."
                          : `${p!.files} ${p!.files === 1 ? "file" : "files"}${p!.directories > 1 ? ` in ${p!.directories} folders` : ""}, ${bytes(p!.bytes)}.${p!.links.length > 0 ? " Library symlinks pointing at it through the mount will break." : ""}`}
            </DialogDescription>
          </DialogHeader>
          {preview.isError && <Failure error={preview.error} />}
          {p && !p.kept && (
            <div className="min-w-0 space-y-3">
              <p className="text-muted-foreground font-mono text-xs break-all">{p.path}</p>
              {p.job && (
                <div className="flex items-center gap-2">
                  <Switch id="forget" checked={forget} onCheckedChange={setForget} />
                  <Label htmlFor="forget">Also remove it from the download history</Label>
                </div>
              )}
            </div>
          )}
          {remove.isError && <Failure error={remove.error} />}
          <DialogFooter>
            <Button variant="outline" onClick={() => setOpen(false)}>
              Cancel
            </Button>
            <Button variant="destructive" disabled={!deletable || remove.isPending} onClick={() => remove.mutate()}>
              {remove.isPending && <Loader2 className="animate-spin" />}
              Remove
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </>
  );
}

function Details({
  id,
  actions,
  onClose,
  onRemoved,
}: {
  id: string;
  actions: Actions;
  onClose: () => void;
  onRemoved: () => void;
}) {
  const client = useQueryClient();
  const details = useQuery({
    queryKey: ["file", id],
    queryFn: () => body(api.GET("/api/v1/files/{id}", { params: { path: { id } } })),
    refetchInterval: (q) => (q.state.data?.media?.pending ? 2000 : false),
  });
  const analyze = useMutation({
    mutationFn: () => body(api.POST("/api/v1/files/{id}/analyze", { params: { path: { id } } })),
    onSuccess: () => {
      toast.success("Analysis queued");
      void client.invalidateQueries({ queryKey: ["file", id] });
    },
    onError: (e) => toast.error(e.message),
  });
  const repair = useMutation({
    mutationFn: () => body(api.POST("/api/v1/files/{id}/repair", { params: { path: { id } } })),
    onSuccess: () => {
      toast.success("Repair started");
      void client.invalidateQueries({ queryKey: ["files"] });
      void client.invalidateQueries({ queryKey: ["file", id] });
    },
    onError: (e) => toast.error(e.message),
  });
  const card = useRef<HTMLDivElement>(null);
  useEffect(() => {
    card.current?.scrollIntoView({
      behavior: matchMedia("(prefers-reduced-motion: reduce)").matches ? "auto" : "smooth",
      block: "nearest",
    });
  }, [details.isSuccess]);

  return (
    <Panel ref={card} className="mt-4 scroll-mt-4 p-4">
      {details.isPending ? (
        <Loading />
      ) : details.isError ? (
        <Failure error={details.error} />
      ) : (
        (() => {
          const { entry, checks, job, links, segments, contents, has_nzb, media } = details.data;
          const file = entry.kind === "file";
          return (
            <div className="space-y-4">
              <div className="flex items-start justify-between gap-3">
                <div className="min-w-0">
                  <h2 className="truncate font-medium" title={entry.name}>
                    <ReleaseName name={entry.name} />
                  </h2>
                  <p className="text-muted-foreground truncate font-mono text-xs" title={entry.path}>
                    {entry.path}
                  </p>
                </div>
                <IconButton label="Close details" onClick={onClose}>
                  <X />
                </IconButton>
              </div>
              <div className="flex flex-wrap gap-2">
                {file && playable(entry.name) && (
                  <Button size="sm" onClick={() => actions.play(entry)}>
                    <Play />
                    Play
                  </Button>
                )}
                {file && (
                  <Button variant="outline" size="sm" onClick={() => void download(entry.path, entry.name)}>
                    <Download />
                    Download
                  </Button>
                )}
                {file && (
                  <Button variant="outline" size="sm" onClick={() => void copyStreamLink(entry.path)}>
                    <Copy />
                    Copy stream link
                  </Button>
                )}
                {has_nzb && (
                  <Button variant="outline" size="sm" asChild>
                    <a href={`/api/v1/files/${id}/nzb`} download>
                      <FileArchive />
                      Export NZB
                    </a>
                  </Button>
                )}
                {file && (
                  <>
                    <Button variant="outline" size="sm" onClick={() => actions.check(id)}>
                      <Stethoscope />
                      Re-check
                    </Button>
                    <ConfirmButton
                      variant="outline"
                      disabled={repair.isPending}
                      label={
                        <>
                          <Wrench />
                          Repair via Arr
                        </>
                      }
                      title="Repair via Arr?"
                      message="The Arr that imported this file is asked for a replacement, even if this one still plays. The file is hidden until the replacement arrives."
                      confirm="Repair via Arr"
                      onConfirm={() => repair.mutate()}
                    />
                  </>
                )}
                {media?.available && (
                  <Button variant="outline" size="sm" disabled={analyze.isPending || media.pending} onClick={() => analyze.mutate()}>
                    <ScanSearch />
                    {media.info || media.outcome ? "Re-analyze" : "Analyze"}
                  </Button>
                )}
                <Remove id={id} onRemoved={onRemoved} />
              </div>
              <div className="grid gap-6 lg:grid-cols-2">
                <dl className="grid grid-cols-[auto_1fr] content-start gap-x-4 gap-y-1.5">
                  <Fact label="Kind">
                    {entry.kind === "directory" ? "Folder" : entry.storage === "multipart" ? "Archive or multipart file" : "File"}
                  </Fact>
                  <Fact label="Size">{entry.size != null ? bytes(entry.size) : "–"}</Fact>
                  {entry.kind !== "directory" && (
                    <Fact label="Encrypted">{entry.encrypted ? "Yes, decrypted on read" : "No"}</Fact>
                  )}
                  {contents && (
                    <Fact label="Contents">
                      {contents.files} files in {contents.directories} folders, {bytes(contents.bytes)}
                    </Fact>
                  )}
                  {entry.added && <Fact label="Added">{stamp(entry.added)}</Fact>}
                  <Fact label="Health">
                    <HealthStatus entry={entry} />
                  </Fact>
                  {entry.last_check && <Fact label="Last check">{stamp(entry.last_check)}</Fact>}
                  {entry.next_check && <Fact label="Next check">{stamp(entry.next_check)}</Fact>}
                  {segments && (
                    <Fact label="Segments">
                      {segments.segments.toLocaleString()}
                      {segments.parts > 1 && ` in ${segments.parts} parts`}
                      {segments.missing > 0 && <span className="text-destructive"> · {segments.missing} missing</span>}
                      {segments.corrupt > 0 && <span className="text-destructive"> · {segments.corrupt} corrupt</span>}
                    </Fact>
                  )}
                  {job && (
                    <Fact label="Job">
                      <span className="break-all">{job.name}</span>
                      <span className="text-muted-foreground"> · {job.status}</span>
                    </Fact>
                  )}
                  {job && <Fact label="NZB">{job.file_name}</Fact>}
                  {(job?.category ?? entry.category) && <Fact label="Category">{job?.category ?? entry.category}</Fact>}
                  <Fact label="ID">
                    <span className="font-mono text-xs">{id}</span>
                  </Fact>
                </dl>
                <div className="space-y-4">
                  {media && <Media media={media} />}
                  {file && <ReadHistory id={id} />}
                  <div>
                    <h3 className="text-muted-foreground mb-1.5 text-xs font-medium tracking-wide uppercase">Mount paths</h3>
                    {links.length === 0 ? (
                      <p className="text-muted-foreground">No stable mount path; library symlinks cannot point here.</p>
                    ) : (
                      <ul className="space-y-0.5">
                        {links.map((l) => (
                          <li key={l} className="truncate font-mono text-xs" title={l}>
                            {l}
                          </li>
                        ))}
                      </ul>
                    )}
                  </div>
                  <div>
                    <h3 className="text-muted-foreground mb-1.5 text-xs font-medium tracking-wide uppercase">Health history</h3>
                    {checks.length === 0 ? (
                      <p className="text-muted-foreground">Not checked yet.</p>
                    ) : (
                      <ul className="space-y-1">
                        {checks.map((c) => (
                          <li key={c.created_at} className="flex items-baseline gap-3">
                            <span className="text-muted-foreground w-28 shrink-0" title={stamp(c.created_at)}>
                              {ago(c.created_at)}
                            </span>
                            <span className="min-w-0">
                              <CheckStatus check={c} />
                              {c.message && <span className="text-muted-foreground block text-xs">{c.message}</span>}
                            </span>
                          </li>
                        ))}
                      </ul>
                    )}
                  </div>
                </div>
              </div>
            </div>
          );
        })()
      )}
    </Panel>
  );
}

export function Explorer() {
  const client = useQueryClient();
  // Links from other pages open one file (`?item=<id>&search=<name>`), or search and open a lone hit.
  const [params] = useSearchParams();
  const [mode, setMode] = useState<"tree" | "list">("tree");
  const [search, setSearch] = useState(params.get("search") ?? "");
  const [health, setHealth] = useState<string>(ANY);
  const [showFilters, setShowFilters] = useState(false);
  const [kind, setKind] = useState<string>(ANY);
  const [repair, setRepair] = useState<string>(ANY);
  const [encrypted, setEncrypted] = useState<string>(ANY);
  const [category, setCategory] = useState("");
  const [minSize, setMinSize] = useState("");
  const [within, setWithin] = useState<string>(ANY);
  const [sort, setSort] = useState<NonNullable<Filters["sort"]>>("name");
  const [order, setOrder] = useState<NonNullable<Filters["order"]>>("asc");
  const [offset, setOffset] = useState(0);
  const [selected, setSelected] = useState<string | undefined>(params.get("item") ?? undefined);
  const [playing, setPlaying] = useState<Entry>();
  const [system, setSystem] = useState(false);

  const minBytes = Number.parseFloat(minSize) * 1e9;
  const filters: Filters = {
    search: search.trim() || undefined,
    state: health === ANY ? undefined : (health as Filters["state"]),
    kind: kind === ANY ? undefined : (kind as Filters["kind"]),
    repair: repair === ANY ? undefined : (repair as Filters["repair"]),
    encrypted: encrypted === ANY ? undefined : encrypted === "yes",
    category: category.trim() || undefined,
    min_size: Number.isFinite(minBytes) && minBytes > 0 ? Math.round(minBytes) : undefined,
    added_after: within === ANY ? undefined : Date.now() - (WITHIN[within] ?? 0),
  };
  const filtered = Object.values(filters).some((v) => v !== undefined);
  // Filters match files anywhere under /content, so they always show a flat list.
  const flat = mode === "list" || filtered;
  // Round the time filter so the query key does not change every render.
  const listQuery = {
    ...filters,
    added_after: filters.added_after && Math.floor(filters.added_after / 60_000) * 60_000,
    sort,
    order,
    limit: PAGE,
    offset,
  };
  const list = useQuery({
    queryKey: ["files", listQuery],
    queryFn: () => files(listQuery),
    enabled: flat,
    placeholderData: keepPreviousData,
  });
  const [pick, setPick] = useState(params.has("search") && !params.has("item"));
  if (pick && list.data && !list.isPlaceholderData) {
    setPick(false);
    if (list.data.entries.length === 1) setSelected(list.data.entries[0]?.item_id ?? undefined);
  }

  const reset = <T,>(set: (v: T) => void) => (v: T) => {
    set(v);
    setOffset(0);
  };
  const actions: Actions = {
    play: setPlaying,
    select: (e) => setSelected(e.item_id ?? undefined),
    check: (id) =>
      void body(api.POST("/api/v1/files/{id}/check", { params: { path: { id } } }))
        .then(() => {
          toast.success("Health check started");
          window.setTimeout(() => {
            void client.invalidateQueries({ queryKey: ["files"] });
            void client.invalidateQueries({ queryKey: ["file", id] });
          }, 3000);
        })
        .catch((e: Error) => toast.error(e.message)),
  };
  const clearFilters = () => {
    setSearch("");
    setHealth(ANY);
    setKind(ANY);
    setRepair(ANY);
    setEncrypted(ANY);
    setCategory("");
    setMinSize("");
    setWithin(ANY);
    setOffset(0);
  };

  const header = (
    <TableHeader>
      <TableRow>
        <TableHead>Name</TableHead>
        <TableHead className="w-8 sm:w-32">
          <span className="sr-only sm:not-sr-only">Health</span>
        </TableHead>
        <TableHead className="hidden w-28 lg:table-cell">Next check</TableHead>
        <TableHead className="hidden w-28 md:table-cell">Added</TableHead>
        <TableHead className="hidden w-24 text-right sm:table-cell">Size</TableHead>
        <TableHead className="w-20">
          <span className="sr-only">Actions</span>
        </TableHead>
      </TableRow>
    </TableHeader>
  );

  return (
    <>
      <PageHeader title="Explorer" description="Everything in the library, as served over WebDAV and the mount" />
      <div className="mb-3 flex flex-wrap items-center gap-2">
        <div className="relative min-w-full flex-1 sm:min-w-56">
          <Search className="text-muted-foreground pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2" aria-hidden />
          <Input
            className="pl-8"
            placeholder="Search file names"
            aria-label="Search file names"
            value={search}
            onChange={(e) => reset(setSearch)(e.target.value)}
          />
        </div>
        {/* Grouped so phones get one row for filtering and one for viewing instead of a ragged wrap. */}
        <div className="flex items-center gap-2">
          <Select value={health} onValueChange={reset(setHealth)}>
            <SelectTrigger className="w-36" aria-label="Health">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value={ANY}>Any health</SelectItem>
              {Object.entries(healthStates).map(([value, [label]]) => (
                <SelectItem key={value} value={value}>
                  {label}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
          <Button variant={showFilters ? "secondary" : "outline"} onClick={() => setShowFilters((s) => !s)}>
            <SlidersHorizontal />
            Filters
          </Button>
        </div>
        <div className="flex items-center gap-2">
          <ToggleGroup
            type="single"
            variant="outline"
            aria-label="View"
            value={flat ? "list" : "tree"}
            onValueChange={(v) => v && setMode(v as "tree" | "list")}
          >
            <ToggleGroupItem value="tree" disabled={filtered} className="data-[state=on]:bg-primary/15 data-[state=on]:text-primary px-3">
              Tree
            </ToggleGroupItem>
            <ToggleGroupItem value="list" className="data-[state=on]:bg-primary/15 data-[state=on]:text-primary px-3">
              List
            </ToggleGroupItem>
          </ToggleGroup>
          <Select value={sort} onValueChange={(v) => reset(setSort)(v as typeof sort)}>
            <SelectTrigger className="w-28 sm:w-32" aria-label="Sort by">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="name">Name</SelectItem>
              <SelectItem value="size">Size</SelectItem>
              <SelectItem value="added">Added</SelectItem>
            </SelectContent>
          </Select>
          <IconButton
            variant="outline"
            size="icon"
            label={order === "asc" ? "Sort descending" : "Sort ascending"}
            onClick={() => reset(setOrder)(order === "asc" ? "desc" : "asc")}
          >
            {order === "asc" ? <ArrowUpNarrowWide /> : <ArrowDownNarrowWide />}
          </IconButton>
          <IconButton variant="outline" size="icon" label="Refresh" onClick={() => void client.invalidateQueries({ queryKey: ["files"] })}>
            <RefreshCw />
          </IconButton>
        </div>
        {!flat && (
          <div className="flex items-center gap-2">
            <Switch id="system" checked={system} onCheckedChange={setSystem} />
            <Label htmlFor="system">System folders</Label>
            <InfoTip label="About system folders">
              Shows /nzbs, /.ids, /completed-symlinks and the README. These are for WebDAV, the mount and the Arrs; most
              actions are unavailable for them here.
            </InfoTip>
          </div>
        )}
      </div>
      {showFilters && (
        <div className="bg-card mb-3 flex flex-wrap items-end gap-3 rounded-lg border p-3">
          <div className="space-y-1">
            <Label htmlFor="kind">Kind</Label>
            <Select value={kind} onValueChange={reset(setKind)}>
              <SelectTrigger id="kind" className="w-40">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={ANY}>Any kind</SelectItem>
                <SelectItem value="nzb_file">Plain file</SelectItem>
                <SelectItem value="multipart">Archive or multipart</SelectItem>
                <SelectItem value="directory">Folder</SelectItem>
              </SelectContent>
            </Select>
          </div>
          <div className="space-y-1">
            <Label htmlFor="repair">Repair</Label>
            <Select value={repair} onValueChange={reset(setRepair)}>
              <SelectTrigger id="repair" className="w-36">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={ANY}>Any repair</SelectItem>
                {Object.entries(checkRepairs).map(([value, label]) => (
                  <SelectItem key={value} value={value}>
                    {label ?? "None"}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
          <div className="space-y-1">
            <Label htmlFor="encryption">Encryption</Label>
            <Select value={encrypted} onValueChange={reset(setEncrypted)}>
              <SelectTrigger id="encryption" className="w-36">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={ANY}>Any encryption</SelectItem>
                <SelectItem value="yes">Encrypted</SelectItem>
                <SelectItem value="no">Not encrypted</SelectItem>
              </SelectContent>
            </Select>
          </div>
          <div className="space-y-1">
            <Label htmlFor="category">Category</Label>
            <Input id="category" className="w-36" value={category} onChange={(e) => reset(setCategory)(e.target.value)} />
          </div>
          <div className="space-y-1">
            <Label htmlFor="min-size">Min size (GB)</Label>
            <Input
              id="min-size"
              className="w-28"
              inputMode="decimal"
              value={minSize}
              onChange={(e) => reset(setMinSize)(e.target.value)}
            />
          </div>
          <div className="space-y-1">
            <Label htmlFor="added">Added</Label>
            <Select value={within} onValueChange={reset(setWithin)}>
              <SelectTrigger id="added" className="w-36">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={ANY}>Any time</SelectItem>
                <SelectItem value="24h">Last 24 hours</SelectItem>
                <SelectItem value="7d">Last 7 days</SelectItem>
                <SelectItem value="30d">Last 30 days</SelectItem>
              </SelectContent>
            </Select>
          </div>
          {filtered && (
            <Button variant="ghost" onClick={clearFilters}>
              <X />
              Clear filters
            </Button>
          )}
        </div>
      )}
      {flat ? (
        list.isPending ? (
          <Loading />
        ) : list.isError ? (
          <Failure error={list.error} />
        ) : list.data.total === 0 && !filtered ? (
          <Empty title="The library is empty">Files show up here once a download is imported.</Empty>
        ) : (
          <>
            {list.data.total > 0 && (
              <p className="text-muted-foreground mb-2 tabular-nums">
                {list.data.total.toLocaleString()} matching · showing {offset + 1}–{offset + list.data.entries.length}
              </p>
            )}
            <Panel className={cn(list.isFetching && "opacity-80")}>
              <Table className="table-fixed">
                {header}
                <TableBody>
                  {list.data.total === 0 && (
                    <TableRow className="[&>td]:hover:bg-inherit">
                      <TableCell colSpan={6} className="py-10 text-center whitespace-normal">
                        <p className="font-medium">No results found</p>
                        <p className="text-muted-foreground mt-1">
                          {filters.search ? <>No files match “{filters.search}” with these filters.</> : "No files match these filters."}
                        </p>
                        <Button variant="outline" size="sm" className="mt-4" onClick={clearFilters}>
                          Clear filters
                        </Button>
                      </TableCell>
                    </TableRow>
                  )}
                  {list.data.entries.map((entry) => (
                    <TableRow key={entry.path} data-state={entry.item_id === selected ? "selected" : undefined}>
                      <Cells
                        entry={entry}
                        actions={actions}
                        name={
                          <button
                            type="button"
                            className="w-full min-w-0 text-left"
                            onClick={() => entry.item_id && actions.select(entry)}
                          >
                            <Name
                              entry={entry}
                              detail={
                                <span className="text-muted-foreground block truncate font-mono text-xs" title={entry.path}>
                                  {entry.path}
                                </span>
                              }
                            />
                          </button>
                        }
                      />
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </Panel>
            <Pager offset={offset} more={offset + PAGE < list.data.total} onChange={setOffset} />
          </>
        )
      ) : (
        <Panel>
          <Table className="table-fixed">
            {header}
            <TableBody>
              <TreeRows path="/" depth={0} sort={{ sort, order }} actions={actions} selected={selected} system={system} />
            </TableBody>
          </Table>
        </Panel>
      )}
      {selected && (
        <Details
          key={selected}
          id={selected}
          actions={actions}
          onClose={() => setSelected(undefined)}
          onRemoved={() => {
            setSelected(undefined);
            void client.invalidateQueries({ queryKey: ["files"] });
          }}
        />
      )}
      {playing && <Player entry={playing} onClose={() => setPlaying(undefined)} />}
    </>
  );
}
