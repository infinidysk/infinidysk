import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Component, type ReactNode, Suspense, useEffect, useRef, useState } from "react";
import { Link, NavLink, Outlet, useLocation } from "react-router";
import {
  Activity,
  ChartColumn,
  CircleAlert,
  CircleArrowUp,
  FolderTree,
  Gauge,
  HardDrive,
  ListOrdered,
  LogOut,
  Menu,
  Monitor,
  Moon,
  RefreshCw,
  Settings,
  Sun,
  Terminal,
  User,
  X,
} from "lucide-react";
import { cn } from "@/lib/utils";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuRadioGroup,
  DropdownMenuRadioItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { api, body } from "./api/client";
import { authKey, useAuth, Wordmark } from "./auth";
import { bytes } from "./format";
import { LibraryBanner } from "./library";
import { MaintenanceNow } from "./maintenance";
import { setTheme, type Theme, useTheme } from "./theme";
import { Loading } from "./ui";

const pages = [
  { to: "/overview", label: "Overview", icon: Gauge },
  { to: "/queue", label: "Queue", icon: ListOrdered },
  { to: "/explorer", label: "Explorer", icon: FolderTree },
  { to: "/health", label: "Health", icon: Activity },
  { to: "/stats", label: "Stats", icon: ChartColumn },
  { to: "/system", label: "System", icon: Terminal },
  { to: "/settings", label: "Settings", icon: Settings },
];

/** Refetches whatever the server says changed. */
function useLiveUpdates() {
  const client = useQueryClient();
  useEffect(() => {
    let source: EventSource;
    let retry: number | undefined;
    const connect = () => {
      source = new EventSource("/api/v1/events");
      for (const list of ["queue", "history", "health"]) {
        source.addEventListener(list, () => void client.invalidateQueries({ queryKey: [list] }));
      }
      // Deleting a file reports a history change only.
      source.addEventListener("history", () => void client.invalidateQueries({ queryKey: ["health", "summary"] }));
      source.addEventListener("resync", () => void client.invalidateQueries());
      source.addEventListener("live", (e) => client.setQueryData(["live"], JSON.parse((e as MessageEvent<string>).data)));
      // Reconnecting usually means the server restarted, perhaps on a new version.
      source.onopen = () => void client.invalidateQueries({ queryKey: ["status"] });
      source.onerror = () => {
        // The browser retries dropped streams itself, but gives up on HTTP errors.
        if (source.readyState !== EventSource.CLOSED) return;
        void client.invalidateQueries({ queryKey: authKey });
        retry = window.setTimeout(connect, 5000);
      };
    };
    connect();
    return () => {
      source.close();
      window.clearTimeout(retry);
    };
  }, [client]);
}

/** The fingerprinted entry script this tab loaded; it changes with every UI build. */
const running = document.querySelector('script[type="module"][src^="/assets/index-"][src$=".js"]')?.getAttribute("src") ?? null;

/** The build this tab last reloaded to reach, so a reload that lands on the old page again is not repeated. */
const RELOADED_FOR = "infinidysk.reloaded-for";

/** The UI build the server serves when it differs from this tab's, and whether reloading already failed to load it. */
function useNewBuild() {
  const served = useQuery({
    queryKey: ["status"],
    queryFn: () => body(api.GET("/api/v1/status")),
    // The dev server serves source, not builds.
    enabled: !import.meta.env.DEV,
    refetchInterval: 5 * 60_000,
    refetchOnWindowFocus: true,
  }).data?.ui_entry;
  if (served == null || running == null || served === running) {
    if (served != null) sessionStorage.removeItem(RELOADED_FOR);
    return { stale: false, stuck: false };
  }
  return { stale: true, stuck: sessionStorage.getItem(RELOADED_FOR) === served, served };
}

/** Moves the tab to a new UI build: at once while hidden, else on the next navigation. */
export function NewBuild() {
  const { stale, stuck, served } = useNewBuild();
  const location = useLocation();
  const seen = useRef(location.key);
  const auto = stale && !stuck;
  useEffect(() => {
    const moved = seen.current !== location.key;
    seen.current = location.key;
    if (!auto || !served) return;
    const reload = () => {
      sessionStorage.setItem(RELOADED_FOR, served);
      window.location.reload();
    };
    if (document.hidden || moved) reload();
    const hide = () => document.hidden && reload();
    document.addEventListener("visibilitychange", hide);
    return () => document.removeEventListener("visibilitychange", hide);
  }, [auto, served, location.key]);
  if (!stale) return null;
  return (
    <Alert className="mb-6">
      <CircleArrowUp />
      <AlertTitle>infinidysk was updated</AlertTitle>
      <AlertDescription>
        {stuck ? (
          <p>
            Reloading still loads the previous version, so a cache between this browser and infinidysk serves an old
            copy of the page. Clear this site's data in the browser, or the cache of your reverse proxy.
          </p>
        ) : (
          <p>This tab still runs the previous version. It reloads when you change pages.</p>
        )}
        <Button size="sm" variant="outline" onClick={() => window.location.reload()}>
          <RefreshCw /> Reload now
        </Button>
      </AlertDescription>
    </Alert>
  );
}

function Brand() {
  return (
    <div className="flex items-center gap-2 font-medium">
      <img src="/logo.svg" alt="" className="size-6" />
      <Wordmark />
    </div>
  );
}

function Version() {
  const status = useQuery({ queryKey: ["status"], queryFn: () => body(api.GET("/api/v1/status")) }).data;
  if (!status) return null;
  const label = status.version;
  return (
    <Link
      to="/system"
      aria-label={`infinidysk ${label}. System${status.update ? ". Update available" : ""}`}
      className={cn(
        "hover:bg-sidebar-accent/60 flex items-center gap-2.5 rounded-md border px-2 py-1.5",
        status.update ? "border-warning" : "border-transparent",
      )}
    >
      <HardDrive className="text-muted-foreground size-4 shrink-0" />
      <span className="min-w-0 flex-1">
        <span className="block text-xs font-medium">infinidysk</span>
        <span className="text-muted-foreground block truncate font-mono text-xs" title={label}>
          {label}
        </span>
      </span>
      {status.update && <CircleArrowUp className="text-warning size-4 shrink-0" />}
    </Link>
  );
}

function Now() {
  // Kept fresh by the `live` server-sent event.
  const live = useQuery({
    queryKey: ["live"],
    queryFn: () => body(api.GET("/api/v1/live")),
    staleTime: Infinity,
  }).data;
  if (!live) return null;
  const busy = live.providers.reduce((n, p) => n + p.active, 0);
  const max = live.providers.reduce((n, p) => n + p.max_connections, 0);
  // `live` and `active` are sampled separately and can briefly disagree.
  const warm = live.providers.reduce((n, p) => n + Math.max(0, p.live - p.active), 0);
  const reads = live.reads.length;
  return (
    <>
      {live.maintenance && <MaintenanceNow live={live.maintenance} />}
      <Link
        to="/overview"
        className="hover:bg-sidebar-accent/60 grid grid-cols-[1fr_auto] gap-x-2 gap-y-0.5 rounded-md px-2 py-1.5 text-xs"
      >
        <span className="text-muted-foreground flex items-center gap-1.5">
          <span className={cn("size-2 rounded-full", reads > 0 ? "bg-success" : "bg-muted-foreground/50")} />
          {reads === 1 ? "1 file open" : `${reads} files open`}
        </span>
        <span className="text-right tabular-nums">{bytes(live.read_bps)}/s</span>
        <span className="text-muted-foreground pl-3">Connections</span>
        <span className="text-right tabular-nums">
          {busy} / {max}
          {warm > 0 && <span className="text-muted-foreground"> · {warm} warm</span>}
        </span>
      </Link>
    </>
  );
}

function Account() {
  const client = useQueryClient();
  const user = useAuth().data?.user;
  const name = user ?? "Authentication off";
  const theme = useTheme();
  const logout = async () => {
    await api.POST("/api/v1/auth/logout");
    await client.invalidateQueries();
  };
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <button
          type="button"
          className="hover:bg-sidebar-accent/60 aria-expanded:bg-sidebar-accent/60 flex h-8 w-full items-center gap-2 rounded-md px-2 font-medium transition-colors"
        >
          <span
            aria-hidden
            className={cn(
              "grid size-5 shrink-0 place-items-center rounded-full text-[0.65rem] font-medium uppercase",
              user ? "bg-primary/15 text-primary" : "bg-foreground/10 text-muted-foreground",
            )}
          >
            {user ? user[0] : <User className="size-3.5" />}
          </span>
          <span className="truncate">{name}</span>
        </button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="start" side="top" className="w-52">
        <DropdownMenuLabel>Theme</DropdownMenuLabel>
        <DropdownMenuRadioGroup value={theme} onValueChange={(value) => setTheme(value as Theme)}>
          <DropdownMenuRadioItem value="system">
            <Monitor /> System
          </DropdownMenuRadioItem>
          <DropdownMenuRadioItem value="light">
            <Sun /> Light
          </DropdownMenuRadioItem>
          <DropdownMenuRadioItem value="dark">
            <Moon /> Dark
          </DropdownMenuRadioItem>
        </DropdownMenuRadioGroup>
        {user && (
          <>
            <DropdownMenuSeparator />
            <DropdownMenuItem onSelect={() => void logout()}>
              <LogOut /> Sign out
            </DropdownMenuItem>
          </>
        )}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

type NavCount = { count: number; label: string; warning?: boolean };

function useNavCounts(): Record<string, NavCount[]> {
  const queue = useQuery({ queryKey: ["queue"], queryFn: () => body(api.GET("/api/v1/queue")) }).data;
  const summary = useQuery({
    queryKey: ["health", "summary"],
    queryFn: () => body(api.GET("/api/v1/health/summary")),
  }).data;
  const arr = useQuery({
    queryKey: ["arr-status"],
    queryFn: () => body(api.GET("/api/v1/arr/status")),
    refetchInterval: 30_000,
  }).data;
  const actionNeeded = summary?.find((s) => s.state === "action_needed")?.files ?? 0;
  const arrIssues = arr?.instances.reduce((n, i) => n + i.queue_issues.length, 0) ?? 0;
  const queueCounts: NavCount[] = [];
  if (queue?.length) queueCounts.push({ count: queue.length, label: `${queue.length.toLocaleString()} in queue` });
  if (arrIssues) {
    const label = arrIssues === 1 ? "1 download needs" : `${arrIssues.toLocaleString()} downloads need`;
    queueCounts.push({ count: arrIssues, label: `${label} you in the Arr`, warning: true });
  }
  return {
    "/queue": queueCounts,
    "/health": actionNeeded ? [{ count: actionNeeded, label: `${actionNeeded} need action`, warning: true }] : [],
  };
}

export function Nav() {
  const counts = useNavCounts();
  return (
    <nav aria-label="Main" className="flex flex-col gap-0.5">
      {pages.map(({ to, label, icon: Icon }) => (
        <NavLink
          key={to}
          to={to}
          className={({ isActive }) =>
            cn(
              "flex h-8 items-center gap-2.5 rounded-md px-2 transition-colors",
              isActive
                ? "bg-sidebar-accent text-sidebar-accent-foreground"
                : "text-sidebar-foreground/80 hover:bg-sidebar-accent/60 hover:text-sidebar-accent-foreground",
            )
          }
        >
          {({ isActive }) => (
            <>
              <Icon className={cn("size-4", isActive && "text-primary")} />
              <span className="flex-1">{label}</span>
              {counts[to]?.map((c) => (
                <span key={c.label} className="contents">
                  <span
                    aria-hidden
                    className={cn(
                      "min-w-5 rounded-full px-1.5 text-center text-xs leading-5 tabular-nums",
                      c.warning ? "bg-warning/15 text-warning-text" : "bg-foreground/10 text-muted-foreground",
                    )}
                  >
                    {c.count.toLocaleString()}
                  </span>
                  <span className="sr-only">, {c.label}</span>
                </span>
              ))}
            </>
          )}
        </NavLink>
      ))}
    </nav>
  );
}

class PageBoundary extends Component<{ children: ReactNode }, { error?: Error }> {
  state: { error?: Error } = {};

  static getDerivedStateFromError(error: Error) {
    return { error };
  }

  render() {
    const { error } = this.state;
    if (!error) return this.props.children;
    return (
      <Alert variant="destructive">
        <CircleAlert />
        <AlertTitle>This page failed to load</AlertTitle>
        <AlertDescription>
          <p className="font-mono text-xs break-all">{error.message}</p>
          <Button size="sm" variant="outline" onClick={() => window.location.reload()}>
            <RefreshCw /> Reload page
          </Button>
        </AlertDescription>
      </Alert>
    );
  }
}

function Footer() {
  return (
    <div className="space-y-1 border-t p-2">
      <Now />
      <Account />
      <Version />
    </div>
  );
}

export function Layout() {
  const location = useLocation();
  // Keyed to the location it opened at, so any navigation closes it, even to the current page.
  const [openAt, setOpenAt] = useState<string>();
  const open = openAt === location.key;
  const menuLabel = open ? "Close menu" : "Open menu";
  useLiveUpdates();

  return (
    <div className="flex min-h-screen">
      <a
        href="#main"
        className="bg-background focus-visible:ring-ring/50 sr-only z-50 rounded-md border px-3 py-2 text-sm font-medium focus-visible:not-sr-only focus-visible:fixed focus-visible:top-2 focus-visible:left-2 focus-visible:ring-3"
      >
        Skip to content
      </a>
      <div className="bg-sidebar sticky top-0 hidden h-screen w-56 shrink-0 flex-col border-r lg:flex">
        <div className="flex h-12 items-center border-b px-4">
          <Brand />
        </div>
        <div className="flex-1 overflow-y-auto p-2">
          <Nav />
        </div>
        <Footer />
      </div>
      <div className="flex min-w-0 flex-1 flex-col">
        <header className="bg-sidebar sticky top-0 z-30 border-b lg:hidden">
          <div className="flex h-12 items-center justify-between px-3">
            <Brand />
            <Tooltip>
              <TooltipTrigger asChild>
                <Button
                  variant="ghost"
                  size="icon-sm"
                  aria-label={menuLabel}
                  aria-expanded={open}
                  aria-controls="menu"
                  onClick={() => setOpenAt(open ? undefined : location.key)}
                >
                  {open ? <X /> : <Menu />}
                </Button>
              </TooltipTrigger>
              <TooltipContent>{menuLabel}</TooltipContent>
            </Tooltip>
          </div>
          {open && (
            <div id="menu" className="max-h-[calc(100dvh-3rem)] overflow-y-auto">
              <div className="border-t p-2">
                <Nav />
              </div>
              <Footer />
            </div>
          )}
        </header>
        <main id="main" tabIndex={-1} className="mx-auto w-full max-w-6xl flex-1 px-4 py-6 outline-none lg:px-8 lg:py-8">
          <NewBuild />
          <LibraryBanner />
          <PageBoundary key={location.pathname}>
            <Suspense fallback={<Loading />}>
              <Outlet />
            </Suspense>
          </PageBoundary>
        </main>
      </div>
    </div>
  );
}
