import {
  type CollisionDetection,
  DndContext,
  type DragEndEvent,
  KeyboardSensor,
  PointerSensor,
  pointerWithin,
  rectIntersection,
  useDraggable,
  useDroppable,
  useSensor,
  useSensors,
} from "@dnd-kit/core";
import { CSS } from "@dnd-kit/utilities";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowDown, Ellipsis, Gauge, GripVertical, Info, Loader2, Pencil, Plus, RotateCcw, Trash2, Zap } from "lucide-react";
import {
  type CSSProperties,
  type FormEvent,
  Fragment,
  type ReactNode,
  type RefObject,
  useCallback,
  useLayoutEffect,
  useRef,
  useState,
} from "react";
import { api, body, type Schemas } from "../api/client";
import { type Settings, useCatalog, useSaveSettings, useSettings } from "../settings";
import { cn } from "@/lib/utils";
import { Alert, AlertDescription } from "@/components/ui/alert";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { ago } from "../format";
import { Empty, Failure, Field, IconButton, InfoTip, Loading, SettingsSection, Status, type Tone } from "../ui";
import { blankProvider, ProviderSheet } from "./provider-sheet";
import { ProviderTestDialog, type TestTarget, useProviderTests } from "./provider-test-dialog";

type Provider = Schemas["Provider"];
type Usage = Schemas["ProviderUsage"];
type ConnectionStatus = Schemas["ProviderStatus"];

const USAGE = ["provider-usage"];
const STATUS = ["provider-status"];

/** Decimal gigabytes, as providers sell quotas. */
const gb = (n: number) => `${(n / 1e9).toFixed(n < 1e10 ? 2 : 1)} GB`;
const day = (ms: number) => new Date(ms).toLocaleDateString(undefined, { month: "short", day: "numeric", timeZone: "UTC" });

/** The level under the pointer; keyboard drags have no pointer and fall back to overlap. */
const collision: CollisionDetection = (args) => {
  const hits = pointerWithin(args);
  return hits.length > 0 ? hits : rectIntersection(args);
};

/** Renumbers priorities 0, 1, 2… keeping their order, so levels stay contiguous. */
function compact(providers: Provider[]): Provider[] {
  const levels = [...new Set(providers.map((p) => p.priority))].sort((a, b) => a - b);
  return providers.map((p) => ({ ...p, priority: levels.indexOf(p.priority) }));
}

function levelsOf(providers: Provider[]): Provider[][] {
  const levels = new Map<number, Provider[]>();
  for (const p of providers) levels.set(p.priority, [...(levels.get(p.priority) ?? []), p]);
  return [...levels.entries()].sort(([a], [b]) => a - b).map(([, list]) => list);
}

type Lane = { color: string; ids: string[]; first: number; last: number };

/** Lane spacing in the backbone gutter, in px. */
const LANE = 12;

/** One lane per backbone shared by 2+ providers, shortest span innermost so lanes nest. */
function lanesOf(ordered: Provider[], colors: Map<string, string>): Lane[] {
  const groups = new Map<string, Lane>();
  ordered.forEach((p, i) => {
    const color = p.backbone ? colors.get(p.backbone) : undefined;
    if (!p.backbone || !color) return;
    const lane = groups.get(p.backbone) ?? { color, ids: [], first: i, last: i };
    lane.ids.push(p.id);
    lane.last = i;
    groups.set(p.backbone, lane);
  });
  return [...groups.values()].filter((l) => l.ids.length > 1).sort((a, b) => a.last - a.first - (b.last - b.first));
}

export function Providers({ title, description }: { title: string; description: string }) {
  const settings = useSettings();
  const catalog = useCatalog();
  const save = useSaveSettings();
  const [editing, setEditing] = useState<Provider | null>(null);
  const tests = useProviderTests();
  const [testing, setTesting] = useState<TestTarget | null>(null);
  const [resetting, setResetting] = useState<Provider | null>(null);
  const [removing, setRemoving] = useState<Provider | null>(null);
  const [dragging, setDragging] = useState(false);
  const rows = useRef(new Map<string, HTMLElement>());
  const usage = useQuery({
    queryKey: USAGE,
    queryFn: () => body(api.GET("/api/v1/provider-usage")),
    refetchInterval: 10_000,
  });
  const status = useQuery({
    queryKey: STATUS,
    queryFn: () => body(api.GET("/api/v1/providers/status")),
    refetchInterval: 5_000,
  });
  const queryClient = useQueryClient();
  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 5 } }),
    useSensor(KeyboardSensor),
  );

  if (settings.isPending || settings.isError || catalog.isPending || catalog.isError)
    return (
      <SettingsSection title={title} description={description}>
        {settings.isError ? <Failure error={settings.error} /> : catalog.isError ? <Failure error={catalog.error} /> : <Loading />}
      </SettingsSection>
    );

  const view = settings.data;
  const locked = view.locked.some((path) => path === "providers" || path.startsWith("providers."));
  // Show the pending save at once, so a drop does not snap back while it saves.
  const providers = (save.isPending ? save.variables?.settings.providers : undefined) ?? view.settings.providers;
  const levels = levelsOf(providers);
  const backbones = new Map(catalog.data.backbones.map((b) => [b.id, b.name]));
  // Hue by catalog position, so a backbone keeps its color across visits.
  const colors = new Map(
    catalog.data.backbones.map((b, i, all) => [b.id, `oklch(0.66 0.15 ${Math.round(20 + (i * 360) / all.length)})`]),
  );
  const lanes = lanesOf(levels.flat(), colors);
  const existing = editing !== null && providers.some((p) => p.id === editing.id);
  const busy = tests.data?.job?.state === "running";
  const speeds = tests.data?.speeds ?? {};
  const add = () => {
    save.reset();
    setEditing(blankProvider(levels.length));
  };

  const store = (next: Provider[], after?: () => void) => {
    const doc: Settings = { ...view.settings, providers: compact(next) };
    save.mutate({ settings: doc, indexers_revision: view.indexers_revision }, {
      onSuccess: () => {
        void queryClient.invalidateQueries({ queryKey: STATUS });
        after?.();
      },
    });
  };
  const onDragEnd = ({ active, over }: DragEndEvent) => {
    setDragging(false);
    if (!over) return;
    const target = over.id === "new" ? levels.length : Number(over.id);
    const moved = providers.find((p) => p.id === active.id);
    if (!moved || moved.priority === target) return;
    // Compacting closes any gap the move leaves behind.
    store(providers.map((p) => (p.id === moved.id ? { ...p, priority: target } : p)));
  };
  const onSave = (provider: Provider) => {
    const next = existing ? providers.map((p) => (p.id === provider.id ? provider : p)) : [...providers, provider];
    store(next, () => setEditing(null));
  };

  return (
    <SettingsSection
      title={title}
      description={description}
      actions={
        <>
          {busy ? (
            <Button variant="outline" onClick={() => setTesting({ id: null, view: true })}>
              <Loader2 className="animate-spin" />
              Test running
            </Button>
          ) : (
            <Button
              variant="outline"
              disabled={locked || !providers.some((p) => p.enabled)}
              onClick={() => setTesting({ id: null })}
            >
              <Zap />
              Auto-tune pipeline
            </Button>
          )}
          <Button disabled={locked} onClick={add}>
            <Plus />
            Add provider
          </Button>
        </>
      }
    >
      <p className="text-muted-foreground">
        Providers on one level share the load; the next level only fetches articles the levels above it miss.{" "}
        <InfoTip label="About backbones">
          Providers on the same backbone store the same articles, so a miss on one counts for all.
        </InfoTip>
      </p>
      {locked && (
        <Alert>
          <Info />
          <AlertDescription>Providers are set by environment variables and cannot be changed here.</AlertDescription>
        </Alert>
      )}
      {save.isError && editing === null && <Failure error={save.error} />}

      {providers.length === 0 ? (
        <Empty
          title="No providers yet"
          action={
            !locked && (
              <Button variant="outline" onClick={add}>
                <Plus />
                Add provider
              </Button>
            )
          }
        >
          Add a Usenet provider to start importing and streaming.
        </Empty>
      ) : (
        <DndContext
          sensors={sensors}
          collisionDetection={collision}
          onDragStart={() => setDragging(true)}
          onDragCancel={() => setDragging(false)}
          onDragEnd={onDragEnd}
        >
          <div
            className="relative flex flex-col sm:pr-(--lanes)"
            style={{ "--lanes": `${lanes.length > 0 ? lanes.length * LANE + 4 : 0}px` } as CSSProperties}
          >
            {levels.map((level, i) => (
              <Fragment key={i}>
                {i > 0 && (
                  <div className="text-muted-foreground flex items-center gap-1.5 py-1.5 pl-4 text-xs">
                    <ArrowDown className="size-3.5" />
                    Articles still missing
                  </div>
                )}
                <Level id={String(i)} title={i === 0 ? "Primary" : `Fallback ${i}`} disabled={locked}>
                  {level.map((p) => {
                    const twin = levels
                      .slice(0, i)
                      .flat()
                      .find((q) => q.enabled && p.backbone != null && q.backbone === p.backbone);
                    return (
                      <Row
                        key={p.id}
                        provider={p}
                        backbone={p.backbone ? backbones.get(p.backbone) : undefined}
                        color={p.backbone ? colors.get(p.backbone) : undefined}
                        twin={twin?.name}
                        rows={rows}
                        speed={speeds[p.id]}
                        usage={usage.data?.providers[p.id]}
                        status={status.data?.find((s) => s.id === p.id)}
                        disabled={locked}
                      >
                        <IconButton
                          label={`Speed test ${p.name}`}
                          disabled={busy}
                          onClick={() => setTesting({ id: p.id, name: p.name })}
                        >
                          <Gauge />
                        </IconButton>
                        <IconButton
                          label={`Edit ${p.name}`}
                          disabled={locked}
                          onClick={() => {
                            save.reset();
                            setEditing(p);
                          }}
                        >
                          <Pencil />
                        </IconButton>
                        {(p.quota || !locked) && (
                          <DropdownMenu>
                            <DropdownMenuTrigger asChild>
                              <IconButton label={`More actions for ${p.name}`}>
                                <Ellipsis />
                              </IconButton>
                            </DropdownMenuTrigger>
                            <DropdownMenuContent align="end">
                              {p.quota && (
                                <DropdownMenuItem onSelect={() => setResetting(p)}>
                                  <RotateCcw />
                                  Reset usage
                                </DropdownMenuItem>
                              )}
                              {p.quota && !locked && <DropdownMenuSeparator />}
                              {!locked && (
                                <DropdownMenuItem variant="destructive" onSelect={() => setRemoving(p)}>
                                  <Trash2 />
                                  Remove
                                </DropdownMenuItem>
                              )}
                            </DropdownMenuContent>
                          </DropdownMenu>
                        )}
                      </Row>
                    );
                  })}
                </Level>
              </Fragment>
            ))}
            {!locked && <NewLevel />}
            <Lanes lanes={lanes} rows={rows} hidden={dragging} />
          </div>
        </DndContext>
      )}

      <ProviderSheet
        provider={editing}
        existing={existing}
        catalog={catalog.data}
        error={editing ? save.error : null}
        saving={save.isPending}
        onSave={onSave}
        onClose={() => setEditing(null)}
      />
      <ProviderTestDialog target={testing} onClose={() => setTesting(null)} />
      <ResetUsageDialog key={resetting?.id} provider={resetting} locked={locked} onClose={() => setResetting(null)} />
      <AlertDialog open={removing !== null} onOpenChange={(open) => !open && setRemoving(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Remove {removing?.name}?</AlertDialogTitle>
            <AlertDialogDescription>Imports and playback stop using this provider right away.</AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction
              variant="destructive"
              onClick={() => removing && store(providers.filter((x) => x.id !== removing.id))}
            >
              Remove
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </SettingsSection>
  );
}

/** Starts a provider's usage over, optionally from an amount already used and with today as the reset day. */
function ResetUsageDialog({ provider, locked, onClose }: { provider: Provider | null; locked: boolean; onClose: () => void }) {
  const client = useQueryClient();
  const [used, setUsed] = useState("");
  const [today, setToday] = useState(false);
  const reset = useMutation({
    mutationFn: ({ id, ...json }: Schemas["ResetUsage"] & { id: string }) =>
      body(api.POST("/api/v1/providers/{id}/usage/reset", { params: { path: { id } }, body: json })),
    onSuccess: (_, { reset_day_today }) => {
      void client.invalidateQueries({ queryKey: USAGE });
      if (reset_day_today) void client.invalidateQueries({ queryKey: ["settings"] });
      onClose();
    },
  });
  const monthly = provider?.quota?.period === "monthly";
  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (!provider) return;
    reset.mutate({
      id: provider.id,
      used_bytes: Math.round(Number(used || 0) * 1e9),
      reset_day_today: monthly && today && !locked,
    });
  };

  return (
    <Dialog open={provider !== null} onOpenChange={(open) => !open && onClose()}>
      <DialogContent className="sm:max-w-md">
        <form className="space-y-4" onSubmit={submit}>
          <DialogHeader>
            <DialogTitle>Reset usage of {provider?.name}</DialogTitle>
            <DialogDescription>Usage starts over from now. A provider that hit its quota is used again right away.</DialogDescription>
          </DialogHeader>
          <Field id="used" label="Already used (GB)" hint="Data used elsewhere this period, if any.">
            <Input id="used" type="number" min={0} step="any" placeholder="0" value={used} onChange={(e) => setUsed(e.target.value)} />
          </Field>
          {monthly && !locked && (
            <div className="flex items-center gap-3">
              <Switch id="today" checked={today} onCheckedChange={setToday} />
              <Label htmlFor="today">Make today the monthly reset day</Label>
            </div>
          )}
          {reset.isError && <Failure error={reset.error} />}
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose}>
              Cancel
            </Button>
            <Button disabled={reset.isPending}>
              {reset.isPending && <Loader2 className="animate-spin" />}
              Reset usage
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

function UsageLine({ usage }: { usage: Usage }) {
  const share = usage.cap ? Math.min(1, usage.used / usage.cap) : 0;
  return (
    <div className="mt-1.5 flex items-center gap-2 text-xs">
      {usage.cap != null && (
        <div className="bg-muted h-1.5 w-24 shrink-0 overflow-hidden rounded-full">
          <div
            className={cn("h-full rounded-full", usage.exhausted ? "bg-destructive" : share > 0.9 ? "bg-warning" : "bg-primary/70")}
            style={{ width: `${share * 100}%` }}
          />
        </div>
      )}
      <span className="text-muted-foreground truncate tabular-nums">
        {usage.cap != null ? `${gb(usage.used)} of ${gb(usage.cap)}` : `${gb(usage.used)} since ${day(usage.since)}`}
        {usage.resets_at != null && usage.cap != null && ` · resets ${day(usage.resets_at)}`}
      </span>
      {usage.exhausted && <Badge variant="destructive">Quota used up</Badge>}
    </div>
  );
}

function Level({ id, title, disabled, children }: { id: string; title: string; disabled: boolean; children: ReactNode }) {
  const { setNodeRef, isOver } = useDroppable({ id, disabled });
  return (
    <section
      ref={setNodeRef}
      className={cn("bg-card rounded-lg border transition-colors", isOver && "border-primary bg-primary/5")}
    >
      <h3 className="text-muted-foreground border-b px-4 py-2 text-xs font-medium tracking-wide uppercase">{title}</h3>
      <ul className="divide-y">{children}</ul>
    </section>
  );
}

/** Dashed lines in the right gutter joining providers on one backbone, across levels. */
function Lanes({ lanes, rows, hidden }: { lanes: Lane[]; rows: RefObject<Map<string, HTMLElement>>; hidden: boolean }) {
  const ref = useRef<SVGSVGElement>(null);
  const [ys, setYs] = useState(new Map<string, number>());
  useLayoutEffect(() => {
    const svg = ref.current;
    if (!svg?.parentElement) return;
    const measure = () => {
      const top = svg.getBoundingClientRect().top;
      const next = new Map<string, number>();
      for (const [id, el] of rows.current) {
        const r = el.getBoundingClientRect();
        next.set(id, r.top - top + r.height / 2);
      }
      setYs(next);
    };
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(svg.parentElement);
    for (const el of rows.current.values()) observer.observe(el);
    return () => observer.disconnect();
  }, [lanes, rows]);

  if (lanes.length === 0) return null;
  const drawn = lanes
    .map((lane, k) => ({ color: lane.color, x: 10 + k * LANE, ys: lane.ids.flatMap((id) => ys.get(id) ?? []) }))
    .filter((lane) => lane.ys.length > 1);
  const line = { strokeWidth: 1.5, strokeDasharray: "2 3" };
  return (
    <svg
      ref={ref}
      aria-hidden
      className={cn(
        "pointer-events-none absolute top-0 right-0 h-full transition-opacity max-sm:hidden",
        hidden && "opacity-0",
      )}
      width={lanes.length * LANE + 4}
    >
      {drawn.map(({ color, x, ys }) =>
        ys.map((y) => <line key={`t${x}-${y}`} x1={0} x2={x} y1={y} y2={y} stroke={color} {...line} />),
      )}
      {drawn.map(({ color, x, ys }) => (
        <line key={`v${x}`} x1={x} x2={x} y1={Math.min(...ys)} y2={Math.max(...ys)} stroke={color} {...line} />
      ))}
      {drawn.map(({ color, x, ys }) =>
        ys.map((y) => (
          <circle key={`d${x}-${y}`} cx={x} cy={y} r={3.5} fill={color} stroke="var(--background)" strokeWidth={2} />
        )),
      )}
    </svg>
  );
}

function NewLevel() {
  const { setNodeRef, isOver } = useDroppable({ id: "new" });
  return (
    <div
      ref={setNodeRef}
      className={cn(
        "mt-3 rounded-lg border border-dashed px-4 py-5 text-center transition-colors",
        isOver ? "border-primary text-primary bg-primary/5" : "text-muted-foreground",
      )}
    >
      Drop here to make a new fallback level
    </div>
  );
}

const connection: Record<ConnectionStatus["state"], { tone: Tone; label: string; hint: string }> = {
  connected: { tone: "success", label: "Connected", hint: "Logged in; the last connection opened fine." },
  waiting: { tone: "muted", label: "Not connected yet", hint: "Nothing has needed this provider since it was saved." },
  paused: { tone: "warning", label: "Paused", hint: "Requests failed, so this provider rests before the next try." },
  failing: { tone: "destructive", label: "Can't connect", hint: "No connection could be opened; the reason is below." },
};

function ConnectionState({ status }: { status: ConnectionStatus }) {
  const { tone, label, hint } = connection[status.state];
  const wait = status.open_for_ms != null ? ` · ${Math.ceil(status.open_for_ms / 1000)} s` : "";
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <span tabIndex={0} className="text-xs">
          <Status tone={tone}>{`${label}${wait}`}</Status>
        </span>
      </TooltipTrigger>
      <TooltipContent>{hint}</TooltipContent>
    </Tooltip>
  );
}

function Row({
  provider,
  backbone,
  color,
  twin,
  rows,
  speed,
  usage,
  status,
  disabled,
  children,
}: {
  provider: Provider;
  backbone: string | undefined;
  color: string | undefined;
  /** An enabled provider on a level above with the same backbone. */
  twin: string | undefined;
  rows: RefObject<Map<string, HTMLElement>>;
  speed: Schemas["Speed"] | undefined;
  usage: Usage | undefined;
  /** Absent for disabled providers. */
  status: ConnectionStatus | undefined;
  disabled: boolean;
  children: ReactNode;
}) {
  const { attributes, listeners, setNodeRef, transform, isDragging } = useDraggable({ id: provider.id, disabled });
  const track = useCallback(
    (el: HTMLElement | null) => {
      setNodeRef(el);
      if (el) rows.current.set(provider.id, el);
      else rows.current.delete(provider.id);
    },
    [setNodeRef, rows, provider.id],
  );
  return (
    <li
      ref={track}
      style={{ transform: CSS.Translate.toString(transform) }}
      className={cn(
        "flex flex-wrap items-center gap-x-3 gap-y-1 px-3 py-2.5",
        isDragging ? "bg-popover relative z-10 rounded-lg border shadow-lg" : "hover:bg-muted/40",
      )}
    >
      {!disabled && (
        <Tooltip>
          <TooltipTrigger asChild>
            <button
              type="button"
              className="text-muted-foreground/60 hover:text-foreground -m-1.5 cursor-grab touch-none p-1.5 active:cursor-grabbing"
              aria-label={`Move ${provider.name}`}
              {...attributes}
              {...listeners}
            >
              <GripVertical className="size-4" />
            </button>
          </TooltipTrigger>
          <TooltipContent>Drag to another level</TooltipContent>
        </Tooltip>
      )}
      <div className={cn("min-w-0 flex-1", !provider.enabled && "opacity-60")}>
        <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
          {/* Badges wrap below a long name instead of squeezing it. */}
          <span className="max-w-full shrink-0 truncate font-medium">{provider.name}</span>
          {!provider.enabled && <Badge variant="outline">Disabled</Badge>}
          {backbone && (
            <Badge variant="secondary">
              {/* A bar like its lane, so it never reads as a status dot. */}
              {color && <span className="h-2.5 w-1 rounded-full" style={{ background: color }} />}
              {backbone}
            </Badge>
          )}
          {provider.enabled && status && <ConnectionState status={status} />}
        </div>
        <div className="text-muted-foreground truncate font-mono text-xs">
          {provider.username && `${provider.username}@`}
          {provider.hostname}:{provider.port}
        </div>
        <div className="text-muted-foreground truncate text-xs">
          {provider.max_connections} connections · depth {provider.pipeline_depth}
          {provider.tls ? "" : " · no TLS"}
          {speed && (
            <span title={`Last speed test ${ago(speed.at)}`}>
              {" · "}
              <span className="text-foreground tabular-nums">{speed.mb_per_s.toFixed(1)} MB/s</span> {ago(speed.at)}
            </span>
          )}
        </div>
        {twin && (
          <div className="text-muted-foreground text-xs">
            Same backbone as {twin}: covers its outages, not its missing articles
          </div>
        )}
        {provider.enabled && status?.state === "failing" && status.error && (
          <div className="text-destructive text-xs">{status.error}</div>
        )}
        {usage && <UsageLine usage={usage} />}
      </div>
      <div className="ml-auto flex shrink-0 gap-0.5 self-start max-sm:[&_button]:size-9">{children}</div>
    </li>
  );
}
