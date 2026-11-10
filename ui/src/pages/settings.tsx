import { type FormEvent, type ReactNode, useEffect, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  ArrowRight,
  CircleAlert,
  Copy,
  Eye,
  KeyRound,
  Loader2,
  Plus,
  Trash2,
  TriangleAlert,
} from "lucide-react";
import { Link, Navigate, NavLink, useBlocker, useNavigate, useParams } from "react-router";
import { toast } from "sonner";
import { cn } from "@/lib/utils";
import { api, body, HttpError, type Schemas } from "../api/client";
import { useAuth } from "../auth";
import { copyText, MASK, notAPassword, type Settings as Doc, useRestart, useSaveSettings, useSettings } from "../settings";
import { LibraryPanel, PreviewDialog, type PreviewRequest, previewTitle } from "../library";
import { BackupsPanel } from "../backups";
import { MaintenancePanel } from "../maintenance";
import { DatabasePanel } from "../database";
import { Alert, AlertAction, AlertDescription } from "@/components/ui/alert";
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
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectGroup, SelectItem, SelectLabel, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Slider } from "@/components/ui/slider";
import { TagInput } from "@/components/ui/tag-input";
import {
  choiceCard,
  DiscardDialog,
  Failure,
  Field,
  IconButton,
  InfoTip,
  Loading,
  Locked,
  OptionCards,
  PageHeader,
  SettingsSection,
  SettingsPanel,
  Status,
  Toggle,
  useDiscardGuard,
} from "../ui";
import { ArrInstances } from "./arr";
import { Indexers } from "./indexers";
import { PerformanceSettings } from "./performance";
import { Providers } from "./providers";

const strategies: { value: Schemas["ImportStrategy"]; title: string; blurb: string }[] = [
  { value: "symlinks", title: "Symlinks", blurb: "Plex, or any media server that sees the mount" },
  { value: "strm", title: "STRM files", blurb: "Emby, Jellyfin or Kodi without the mount" },
];

const inlineLink = "hover:text-foreground relative underline underline-offset-4 after:absolute after:-inset-y-1 after:inset-x-0";

function randomKey(): string {
  return Array.from(crypto.getRandomValues(new Uint8Array(16)), (b) => b.toString(16).padStart(2, "0")).join("");
}

const isObject = (v: unknown): v is Record<string, unknown> => typeof v === "object" && v !== null && !Array.isArray(v);
const same = (a: unknown, b: unknown) => JSON.stringify(a) === JSON.stringify(b);

/** Moves `draft` onto `next`: takes the server's value wherever the user left `base` untouched. */
function rebase(draft: unknown, base: unknown, next: unknown): unknown {
  if (same(draft, base)) return next;
  if (!isObject(draft) || !isObject(base) || !isObject(next)) return draft;
  return Object.fromEntries(Object.keys(next).map((k) => [k, rebase(draft[k], base[k], next[k])]));
}

const categories = [
  {
    group: "Platform",
    slug: "providers",
    title: "Providers",
    description: "Usenet servers used for imports, health checks and playback",
  },
  {
    group: "Platform",
    slug: "webdav",
    title: "WebDAV",
    description: "Credentials for media servers and other WebDAV clients",
  },
  { group: "Platform", slug: "mount", title: "Mount", description: "Where the library appears on this machine, as the Arrs see it" },
  {
    group: "Platform",
    slug: "performance",
    title: "Performance",
    description: "Read-ahead, caching and download speed",
  },
  {
    group: "Automation",
    slug: "arr",
    title: "Arr instances",
    description: "Used to repair: when a file loses articles, the Arr that imported it is asked for a replacement",
  },
  {
    group: "Automation",
    slug: "indexers",
    title: "Indexers",
    description: "Manage Newznab indexers and test their connections",
  },
  {
    group: "Automation",
    slug: "library",
    title: "Library",
    description: "How imported files reach your media server, and the library folders they land in",
  },
  {
    group: "Automation",
    slug: "imports",
    title: "Imports",
    description: "How queued NZBs are checked and handed to your Arrs",
  },
  {
    group: "Automation",
    slug: "health",
    title: "Health monitoring",
    description: "Checks that imported files are still complete at your providers, and what happens when they are not",
  },
  {
    group: "Automation",
    slug: "download-client",
    title: "Download client",
    description: "Add infinidysk to Sonarr, Radarr and the other Arrs as a SABnzbd client with these details",
  },
  {
    group: "System",
    slug: "access",
    title: "Access",
    description: "Who must sign in to this web UI and the admin API, and the key scripts use. WebDAV and the download client keep their own credentials",
  },
  {
    group: "System",
    slug: "network",
    title: "Network",
    description: "How Arrs and browsers reach infinidysk, and how it identifies clients behind a reverse proxy",
  },
  {
    group: "System",
    slug: "database",
    title: "Database",
    description: "Where infinidysk keeps its library, queue and history, and moving it to PostgreSQL",
  },
  {
    group: "System",
    slug: "backups",
    title: "Backups",
    description: "Copies of the library database you can download, upload and restore",
  },
  {
    group: "System",
    slug: "maintenance",
    title: "Maintenance",
    description: "Housekeeping jobs you can run now, try as a dry run, or schedule for a daily window",
  },
  {
    group: "System",
    slug: "experiments",
    title: "Experiments",
    description: "Unfinished streaming changes you can switch on while they are measured",
  },
] as const;

const groups = [...new Set(categories.map((c) => c.group))];

type Category = (typeof categories)[number];

function Section({ category, children }: { category: Category; children: ReactNode }) {
  return (
    <SettingsSection title={category.title} description={category.description}>
      {children}
    </SettingsSection>
  );
}

function CategoryNav({ current }: { current: Category }) {
  const navigate = useNavigate();
  return (
    <div>
      <Select value={current.slug} onValueChange={(slug) => void navigate(`/settings/${slug}`)}>
        <SelectTrigger aria-label="Settings category" className="w-full md:hidden">
          <SelectValue />
        </SelectTrigger>
        <SelectContent>
          {groups.map((group) => (
            <SelectGroup key={group}>
              <SelectLabel>{group}</SelectLabel>
              {categories
                .filter((c) => c.group === group)
                .map((c) => (
                  <SelectItem key={c.slug} value={c.slug}>
                    {c.title}
                  </SelectItem>
                ))}
            </SelectGroup>
          ))}
        </SelectContent>
      </Select>
      <nav aria-label="Settings" className="hidden flex-col gap-5 md:flex">
        {groups.map((group) => (
          <div key={group} className="flex flex-col gap-0.5">
            <p className="text-muted-foreground px-2 pb-1 text-xs font-medium tracking-wide uppercase">{group}</p>
            {categories
              .filter((c) => c.group === group)
              .map((c) => (
                <NavLink
                  key={c.slug}
                  to={`/settings/${c.slug}`}
                  className={({ isActive }) =>
                    cn(
                      "flex h-8 items-center rounded-md px-2 whitespace-nowrap transition-colors",
                      isActive ? "bg-accent text-accent-foreground font-medium" : "text-muted-foreground hover:bg-accent/60 hover:text-foreground",
                    )
                  }
                >
                  {c.title}
                </NavLink>
              ))}
          </div>
        ))}
      </nav>
    </div>
  );
}

/** A stored secret shows as hidden; typing replaces it, and keys can be revealed or generated. */
function SecretField({
  id,
  label,
  value,
  locked,
  readOnly,
  generate,
  reveal,
  noAutofill,
  hint,
  tip,
  error,
  action,
  onChange,
}: {
  id: string;
  label: string;
  value: string;
  locked: boolean;
  /** Shown and copied only; `action` changes it. */
  readOnly?: boolean;
  generate?: boolean;
  /** Settings path of a key the server may reveal. */
  reveal?: "sab.api_key" | "auth.api_key" | "webdav.download_key" | "imports.strm_key";
  /** Not a sign-in password, so password managers stay away. */
  noAutofill?: boolean;
  hint?: string;
  tip?: string;
  error?: string;
  action?: ReactNode;
  onChange: (value: string) => void;
}) {
  const [shown, setShown] = useState(false);
  const [revealed, setRevealed] = useState<string>();
  const [failed, setFailed] = useState<string>();
  const stored = value === MASK;
  const plain = stored ? revealed : value;
  const name = /^[A-Z]{2}/.test(label) ? label : label.toLowerCase();
  // A plain password is a short control; keys with buttons need the full width.
  const short = !generate && !reveal && !action;
  const show = async () => {
    if (!reveal) return;
    setFailed(undefined);
    try {
      const key = await body(api.GET("/api/v1/settings/keys/{name}", { params: { path: { name: reveal } } }));
      setRevealed(key.value);
    } catch (e) {
      setFailed(e instanceof Error ? e.message : "Could not reveal the key");
    }
  };
  return (
    <Field
      id={id}
      layout={short ? "row" : undefined}
      label={
        <>
          {label}
          {tip && <InfoTip label={`About ${name}`}>{tip}</InfoTip>}
          <Locked locked={locked} />
        </>
      }
      hint={!stored && value && generate && !reveal ? "Copy it now; it is hidden once saved." : hint}
      error={failed ?? error}
    >
      <div className="flex flex-wrap gap-2">
        <Input
          id={id}
          type={shown || (stored && revealed) ? "text" : "password"}
          className={cn("flex-1 font-mono", !short && "min-w-64")}
          {...(reveal || noAutofill ? notAPassword : { autoComplete: "new-password" })}
          disabled={locked || readOnly}
          placeholder={stored ? "••••••••" : "Not set"}
          value={stored ? (revealed ?? "") : value}
          onChange={(e) => {
            setRevealed(undefined);
            onChange(e.target.value || (stored ? MASK : ""));
          }}
        />
        {reveal && stored && !revealed && (
          <IconButton variant="outline" size="icon" label={`Reveal ${name}`} onClick={() => void show()}>
            <Eye />
          </IconButton>
        )}
        {generate && !locked && (
          <Button
            type="button"
            variant="outline"
            onClick={() => {
              setRevealed(undefined);
              onChange(randomKey());
              setShown(true);
            }}
          >
            <KeyRound />
            Generate
          </Button>
        )}
        {plain && (
          <IconButton
            variant="outline"
            size="icon"
            label={`Copy ${name}`}
            onClick={() =>
              void copyText(plain)
                .then(() => toast.success("Copied"))
                .catch((e: Error) => toast.error(e.message))
            }
          >
            <Copy />
          </IconButton>
        )}
        {!locked && action}
      </div>
    </Field>
  );
}

/** A value to paste elsewhere: shown read-only beside its label, with a Copy button. */
function CopyField({ id, label, value, placeholder, hint }: { id: string; label: string; value: string; placeholder?: string; hint?: ReactNode }) {
  return (
    <Field id={id} layout="row" label={label} hint={hint}>
      <div className="flex gap-2">
        <Input id={id} readOnly className="font-mono" placeholder={placeholder} value={value} />
        {value && (
          <IconButton
            variant="outline"
            size="icon"
            label={`Copy ${label.toLowerCase()}`}
            onClick={() =>
              void copyText(value)
                .then(() => toast.success("Copied"))
                .catch((e: Error) => toast.error(e.message))
            }
          >
            <Copy />
          </IconButton>
        )}
      </div>
    </Field>
  );
}

/** A setting that changes library files: saved through a preview, not the Save button. */
function PreviewedInput({
  id,
  saved,
  locked,
  placeholder,
  onPreview,
}: {
  id: string;
  saved: string;
  locked: boolean;
  placeholder?: string;
  onPreview: (value: string) => void;
}) {
  const [value, setValue] = useState(saved);
  useEffect(() => setValue(saved), [saved]);
  return (
    <div className="flex gap-2">
      <Input
        id={id}
        className="font-mono"
        placeholder={placeholder}
        disabled={locked}
        value={value}
        onChange={(e) => setValue(e.target.value)}
      />
      {value.trim() !== saved && (
        <Button type="button" variant="outline" onClick={() => onPreview(value.trim())}>
          Preview change
        </Button>
      )}
    </div>
  );
}

type LibraryDir = Schemas["LibraryDir"];

/** Library folders as seen here, each with the path the Arrs see it under. */
function LibraryDirs({ value, locked, onChange }: { value: LibraryDir[]; locked: boolean; onChange: (value: LibraryDir[]) => void }) {
  const set = (i: number, dir: Partial<LibraryDir>) => onChange(value.map((d, j) => (j === i ? { ...d, ...dir } : d)));
  return (
    <div className="space-y-2">
      {value.length === 0 && <p className="text-muted-foreground text-xs">None. Switching changes only new downloads.</p>}
      {value.length > 0 && (
        <div className="text-muted-foreground hidden grid-cols-[1fr_1fr_2rem] gap-2 text-xs sm:grid" aria-hidden>
          <span>Folder here</span>
          <span>As the Arrs see it</span>
        </div>
      )}
      {value.map((d, i) => (
        <div key={i} className="grid grid-cols-[1fr_auto] gap-2 sm:grid-cols-[1fr_1fr_auto]">
          <Input
            aria-label="Folder here"
            className="font-mono"
            placeholder="/media/tv"
            pattern="/.*"
            required
            disabled={locked}
            value={d.path}
            onChange={(e) => set(i, { path: e.target.value })}
          />
          <Input
            aria-label="Folder as the Arrs see it"
            className="order-last col-span-2 font-mono sm:order-none sm:col-span-1"
            pattern="/.*"
            disabled={locked}
            value={d.arr_path}
            onChange={(e) => set(i, { arr_path: e.target.value })}
          />
          <IconButton
            size="icon"
            label={`Remove folder ${i + 1}`}
            disabled={locked}
            onClick={() => onChange(value.filter((_, j) => j !== i))}
          >
            <Trash2 />
          </IconButton>
        </div>
      ))}
      <Button type="button" variant="outline" size="sm" disabled={locked} onClick={() => onChange([...value, { path: "", arr_path: "" }])}>
        <Plus /> Add folder
      </Button>
    </div>
  );
}

function plural(n: number, one: string) {
  return `${n} ${one}${n === 1 ? "" : "s"}`;
}

/** Offers a restart when a save starts needing one, and keeps offering it until then. */
function RestartBanner({ required }: { required: boolean }) {
  const [open, setOpen] = useState(false);
  const was = useRef(required);
  useEffect(() => {
    if (required && !was.current) setOpen(true);
    was.current = required;
  }, [required]);
  const restart = useRestart();
  // Kept fresh by the `live` server-sent event.
  const live = useQuery({ queryKey: ["live"], queryFn: () => body(api.GET("/api/v1/live")), staleTime: Infinity }).data;
  const reads = live?.reads.length ?? 0;
  const imports = live?.imports.length ?? 0;
  const run = () =>
    restart.mutate(undefined, {
      onSuccess: () => {
        toast.success("Restarted");
        setOpen(false);
      },
    });

  return (
    <>
      {required && (
        <Alert className="mb-6">
          <TriangleAlert className="text-warning-text" />
          <AlertDescription>Some changes take effect after a restart.</AlertDescription>
          <AlertAction>
            <Button type="button" variant="outline" size="xs" disabled={restart.isPending} onClick={() => setOpen(true)}>
              Restart
            </Button>
          </AlertAction>
        </Alert>
      )}
      <AlertDialog open={open} onOpenChange={(o) => !restart.isPending && setOpen(o)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Restart infinidysk now?</AlertDialogTitle>
            <AlertDialogDescription>
              The mount directory, mount file dates, read-ahead memory, FUSE tuning and experiments apply after a restart, which takes a few seconds.{" "}
              {reads + imports === 0
                ? "Nothing is streaming or importing right now."
                : [
                    reads > 0 && `${plural(reads, "open stream")} will stop; press play again afterwards.`,
                    imports > 0 && `${plural(imports, "import")} will continue after the restart.`,
                  ]
                    .filter(Boolean)
                    .join(" ")}
            </AlertDialogDescription>
          </AlertDialogHeader>
          {restart.isError && <Failure error={restart.error} />}
          <AlertDialogFooter>
            <AlertDialogCancel disabled={restart.isPending}>Cancel</AlertDialogCancel>
            <AlertDialogAction
              disabled={restart.isPending}
              onClick={(e) => {
                e.preventDefault();
                run();
              }}
            >
              {restart.isPending ? (
                <>
                  <Loader2 className="animate-spin" /> Restarting…
                </>
              ) : (
                "Restart now"
              )}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </>
  );
}

export function Settings() {
  const { category: slug } = useParams();
  const category = categories.find((c) => c.slug === slug);
  const settings = useSettings();
  const save = useSaveSettings();
  const auth = useAuth();
  const status = useQuery({
    queryKey: ["status"],
    queryFn: () => body(api.GET("/api/v1/status")),
    enabled: slug === "mount",
  }).data;
  const [draft, setDraft] = useState<Doc | null>(null);
  const [preview, setPreview] = useState<PreviewRequest | null>(null);
  const [refused, setRefused] = useState<PreviewRequest | null>(null);
  // The server document the draft started from, so updates from elsewhere keep the user's edits.
  const base = useRef<Doc | null>(null);
  const baseRevision = useRef<string | null>(null);
  useEffect(() => {
    if (!settings.data) return;
    const next = settings.data.settings;
    // React may run the updater after this effect returns, so it must not read the ref.
    const prev = base.current;
    setDraft((d) => (d && prev ? (rebase(d, prev, next) as Doc) : next));
    base.current = next;
    baseRevision.current = settings.data.indexers_revision;
  }, [settings.data]);
  const dirty = !!draft && !!settings.data && !same(draft, settings.data.settings);
  const blocker = useBlocker(({ currentLocation, nextLocation }) => dirty && currentLocation.pathname !== nextLocation.pathname);
  useEffect(() => {
    if (!dirty) return;
    const warn = (e: BeforeUnloadEvent) => e.preventDefault();
    addEventListener("beforeunload", warn);
    return () => removeEventListener("beforeunload", warn);
  }, [dirty]);

  if (slug === "admin-api" || slug === "authentication") return <Navigate to="/settings/access" replace />;
  if (slug === "support") return <Navigate to="/system" replace />;
  if (!category) return <Navigate to={`/settings/${categories[0].slug}`} replace />;
  if (settings.isPending) return <Loading />;
  if (settings.isError) return <Failure error={settings.error} />;
  if (!draft) return <Loading />;

  const view = settings.data;
  const locked = (path: string) => view.locked.some((l) => l === path || path.startsWith(`${l}.`) || l.startsWith(`${path}.`));
  const patch = <K extends keyof Doc>(key: K, value: Partial<Doc[K]>) =>
    setDraft((d) => (d ? { ...d, [key]: { ...d[key], ...value } } : d));
  // The server may rewrite what it stores (secrets come back masked), so the saved document replaces the draft.
  const reset = (doc: Doc) => {
    setDraft(doc);
    setRefused(null);
  };
  const saved = view.settings;
  const ssoSaved = saved.auth.oidc.issuer !== "";
  const proven = auth.data?.method === "sso" && auth.data.sso_current;
  // What the SSO proof covers: not the display name, and the public URL only while it supplies the redirect URI.
  const proofFields = ({ display_name: _, ...rest }: Schemas["Oidc"]) => rest;
  const ssoEdited =
    !same(proofFields(draft.auth.oidc), proofFields(saved.auth.oidc)) ||
    (!saved.auth.oidc.redirect_uri && draft.network.public_url !== saved.network.public_url);
  // Only saved-on → off needs proof; with password sign-in already off, SSO stays editable.
  const passwordOff =
    (saved.auth.local_login || !ssoSaved) && !(draft.auth.local_login || draft.auth.oidc.issuer.trim() === "");
  const passwordOffRefusal = !passwordOff
    ? undefined
    : ssoEdited
      ? "Save the SSO changes and sign in with SSO again before turning password sign-in off."
      : !proven
        ? "Sign in with SSO using the saved settings before turning password sign-in off."
        : undefined;
  // Setting SSO up keeps password sign-in on until an SSO sign-in has worked.
  const oidc = (v: Partial<Schemas["Oidc"]>) =>
    patch("auth", { oidc: { ...draft.auth.oidc, ...v }, ...(!ssoSaved && { local_login: true }) });
  const callback = `${draft.network.public_url || window.location.origin}/auth/oidc/callback`;
  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (passwordOffRefusal) {
      toast.error(passwordOffRefusal);
      return;
    }
    save.mutate({ settings: draft, indexers_revision: baseRevision.current ?? view.indexers_revision }, {
      onSuccess: (view) => {
        reset(view.settings);
        toast.success("Settings saved");
      },
    });
  };
  const label = (text: string, path: string, tip?: ReactNode) => (
    <>
      {text}
      {tip && <InfoTip label={`About ${text.toLowerCase()}`}>{tip}</InfoTip>}
      <Locked locked={locked(path)} />
    </>
  );
  /** The save error, on the field it is about. */
  const invalid = (path: string) =>
    save.error instanceof HttpError && save.error.path === path ? save.error.message : undefined;
  const address = new URL(URL.canParse(saved.network.public_url) ? saved.network.public_url : location.origin);
  const addressHint = saved.network.public_url ? "From the Public URL." : "As this browser reaches infinidysk.";
  /** Changes that convert library files go through a preview of the saved settings. */
  const previewChange = (req: PreviewRequest) => {
    if (dirty) setRefused(req);
    else setPreview(req);
  };

  return (
    <>
      <PageHeader title="Settings" description="Saved changes apply right away unless noted" tab={[category.title, "Settings"]} />
      <RestartBanner required={view.restart_required} />
      <div className="grid gap-6 md:grid-cols-[12rem_1fr] md:gap-10">
        <CategoryNav current={category} />
        <div className="min-w-0">
          {category.slug === "providers" && <Providers title={category.title} description={category.description} />}
          {category.slug === "arr" && <ArrInstances title={category.title} description={category.description} />}
          {category.slug === "indexers" && <Indexers title={category.title} description={category.description} />}
          {category.slug === "database" && <DatabasePanel title={category.title} description={category.description} />}
          <form id="settings" onSubmit={submit}>
            {category.slug === "download-client" && (
              <Section category={category}>
                <SettingsPanel title="Connection">
                  <CopyField id="sab-host" label="Host" value={address.hostname} hint={addressHint} />
                  <CopyField
                    id="sab-port"
                    label="Port"
                    value={address.port || (address.protocol === "https:" ? "443" : "80")}
                  />
                  <Field id="sab-ssl" layout="row" label="Use SSL">
                    <Input id="sab-ssl" readOnly value={address.protocol === "https:" ? "Yes" : "No"} />
                  </Field>
                  <CopyField
                    id="sab-url-base"
                    label="URL base"
                    value={address.pathname.replace(/\/+$/, "")}
                    placeholder="Leave empty"
                  />
                  <SecretField
                    id="sab-key"
                    label="API key"
                    value={draft.sab.api_key}
                    locked={locked("sab.api_key")}
                    generate
                    reveal="sab.api_key"
                    onChange={(api_key) => patch("sab", { api_key })}
                  />
                </SettingsPanel>
                <SettingsPanel title="Categories">
                  <Field
                    id="categories"
                    label={label("Categories", "sab.categories")}
                    hint="Categories available to SAB clients for organizing imported NZBs. Only letters, numbers, and dashes are allowed."
                  >
                    <TagInput
                      id="categories"
                      disabled={locked("sab.categories")}
                      value={draft.sab.categories}
                      onChange={(categories) => patch("sab", { categories })}
                    />
                  </Field>
                  <Field
                    id="manual-category"
                    layout="row"
                    label={label("Default category", "sab.manual_category")}
                    hint="For NZBs sent without a category."
                    error={invalid("sab.manual_category")}
                  >
                    <Select
                      disabled={locked("sab.manual_category")}
                      value={draft.sab.manual_category}
                      onValueChange={(manual_category) => patch("sab", { manual_category })}
                    >
                      <SelectTrigger id="manual-category" className="w-full">
                        <SelectValue />
                      </SelectTrigger>
                      <SelectContent>
                        {[...new Set([saved.sab.manual_category, ...draft.sab.categories])].map((c) => (
                          <SelectItem key={c} value={c}>
                            {c}
                          </SelectItem>
                        ))}
                      </SelectContent>
                    </Select>
                  </Field>
                </SettingsPanel>
              </Section>
            )}

            {category.slug === "library" && (
              <Section category={category}>
                <div className="space-y-1.5">
                  <Label>{label("Import strategy", "imports.strategy")}</Label>
                  <div className="grid gap-2 sm:grid-cols-2">
                    {strategies.map(({ value, title, blurb }) =>
                      saved.imports.strategy === value ? (
                        <div key={value} className={cn(choiceCard, "border-primary ring-primary ring-1")}>
                          <span className="flex items-center gap-1.5 font-medium">
                            {title}
                            <Badge variant="secondary">Current</Badge>
                          </span>
                          <span className="text-muted-foreground mt-1 block text-xs">{blurb}</span>
                        </div>
                      ) : (
                        <button
                          key={value}
                          type="button"
                          disabled={locked("imports.strategy")}
                          onClick={() => previewChange({ strategy: value })}
                          className={cn(choiceCard, "hover:bg-accent/60")}
                        >
                          <span className="font-medium">{title}</span>
                          <span className="text-muted-foreground mt-1 block text-xs">{blurb}</span>
                          <span className="text-primary mt-2 flex items-center gap-1 text-xs font-medium">
                            Switch to {title}
                            <ArrowRight className="size-3.5" />
                          </span>
                        </button>
                      ),
                    )}
                  </div>
                  <p className="text-muted-foreground text-xs">
                    {saved.imports.strategy === "symlinks" ? (
                      <>
                        Symlinks point into the mount, so it must be up here and on the media server's host (
                        <Link to="/settings/mount" className={inlineLink}>
                          Settings → Mount
                        </Link>
                        ).
                      </>
                    ) : (
                      <>
                        Each video becomes a small file holding a playback link to infinidysk, so media servers need no mount.
                        Release subtitles are copied beside it.
                      </>
                    )}{" "}
                    Switching shows a preview, then converts the files in your library folders.
                  </p>
                </div>
                <LibraryPanel onPreview={previewChange} />
                {saved.imports.strategy === "strm" &&
                  draft.arr_instances.some((a) => a.enabled && (a.profile === "lidarr" || a.profile === "chaptarr")) && (
                    <Alert>
                      <TriangleAlert className="text-warning-text" />
                      <AlertDescription>
                        Lidarr and Chaptarr cannot import STRM files, so their downloads still arrive as symlinks, which need the
                        mount.
                      </AlertDescription>
                    </Alert>
                  )}
                <SettingsPanel title="Folders">
                  <Field
                    label={label(
                      "Library folders",
                      "imports.library_dirs",
                      "Switching converts the symlinks and STRM files in them; nothing outside these folders is touched.",
                    )}
                    hint="Your Arrs' library folders. Leave “As the Arrs see it” empty when the path is the same."
                    error={invalid("imports.library_dirs")}
                  >
                    <LibraryDirs
                      value={draft.imports.library_dirs}
                      locked={locked("imports.library_dirs")}
                      onChange={(library_dirs) => patch("imports", { library_dirs })}
                    />
                  </Field>
                </SettingsPanel>
                {saved.imports.strategy === "strm" && (
                  <SettingsPanel title="STRM">
                    <Field
                      id="strm-dir"
                      label={label("Completed STRM directory", "imports.strm_dir")}
                      hint="An absolute path, shared with your Arrs: each download's STRM files are written here for them to import. Created if missing."
                      error={invalid("imports.strm_dir")}
                    >
                      <Input
                        id="strm-dir"
                        className="font-mono"
                        required
                        pattern="/.*"
                        disabled={locked("imports.strm_dir")}
                        value={draft.imports.strm_dir}
                        onChange={(e) => patch("imports", { strm_dir: e.target.value })}
                      />
                    </Field>
                    <Field
                      label="Playback address"
                      hint={
                        <>
                          The Public URL from{" "}
                          <Link to="/settings/network" className={inlineLink}>
                            Settings → Network
                          </Link>
                          . Media servers, and players that open the link directly, must reach it.
                        </>
                      }
                    >
                      {draft.network.public_url ? (
                        <p className="bg-muted/50 truncate rounded-md border px-2.5 py-1.5 font-mono text-sm">
                          {draft.network.public_url}
                        </p>
                      ) : (
                        <Alert variant="destructive">
                          <CircleAlert />
                          <AlertDescription>Set a Public URL first; STRM files cannot be written without it.</AlertDescription>
                        </Alert>
                      )}
                    </Field>
                    <SecretField
                      id="strm-key"
                      label="STRM key"
                      value={draft.imports.strm_key}
                      locked={locked("imports.strm_key")}
                      readOnly
                      reveal="imports.strm_key"
                      hint="Signs the playback link in each STRM file."
                      tip="A new key rewrites every STRM file; the previous key keeps playing until nothing used it for 7 days."
                      action={
                        <Button type="button" variant="outline" onClick={() => previewChange({ new_key: true })}>
                          <KeyRound />
                          New key
                        </Button>
                      }
                      onChange={(strm_key) => patch("imports", { strm_key })}
                    />
                  </SettingsPanel>
                )}
              </Section>
            )}

            {category.slug === "imports" && (
              <Section category={category}>
                <SettingsPanel title="Imports">
                  <Field
                    id="queue-concurrency"
                    layout="row"
                    label={
                      <span className="flex w-full justify-between">
                        {label("Imports at once", "queue.concurrency", "Playback always goes first. Lowering it lets running imports finish.")}
                        <span className="font-mono tabular-nums">{draft.queue.concurrency}</span>
                      </span>
                    }
                    hint="More clear a long queue sooner but share your provider connections."
                    error={invalid("queue.concurrency")}
                  >
                    <Slider
                      id="queue-concurrency"
                      aria-label="Imports at once"
                      min={1}
                      max={10}
                      step={1}
                      disabled={locked("queue.concurrency")}
                      value={[draft.queue.concurrency]}
                      onValueChange={([concurrency]) => patch("queue", { concurrency })}
                    />
                  </Field>
                  <Field
                    label={label("Blocked files", "queue.extra_blocklist")}
                    hint={
                      <>
                        Filename patterns left out of every import, on top of the built-in list of executables and junk.{" "}
                        <code>*</code> matches any run of characters and <code>?</code> one; case is ignored.
                      </>
                    }
                  >
                    <TextList
                      value={draft.queue.extra_blocklist}
                      locked={locked("queue.extra_blocklist")}
                      onChange={(extra_blocklist) => patch("queue", { extra_blocklist })}
                      item="Blocked pattern"
                      placeholder="*.txt"
                      empty="None. Only the built-in list applies."
                    />
                  </Field>
                  <Field
                    id="queue-retention"
                    layout="row"
                    label={label(
                      "Keep unused NZBs (days)",
                      "queue.nzb_retention_days",
                      "So the same NZB sent again reuses it. History itself is forgotten after 30 days; downloads stay.",
                    )}
                    hint="How long an NZB stays stored after its job and files are gone. 0 deletes it at once."
                    error={invalid("queue.nzb_retention_days")}
                  >
                    <Input
                      id="queue-retention"
                      type="number"
                      className="w-28"
                      min={0}
                      max={3650}
                      required
                      disabled={locked("queue.nzb_retention_days")}
                      value={draft.queue.nzb_retention_days}
                      onChange={(e) => patch("queue", { nzb_retention_days: Number(e.target.value) })}
                    />
                  </Field>
                </SettingsPanel>
                <SettingsPanel title="Arr queue">
                  <Toggle
                    id="queue-management"
                    label={label(
                      "Clear stuck imports",
                      "arr.queue_management",
                      "Such as samples or files the Arr already has. Bad releases are blocklisted so the Arr searches again; anything else stays for you to settle.",
                    )}
                    hint="Removes finished downloads the Arr will not import for a known reason."
                    disabled={locked("arr.queue_management")}
                    checked={draft.arr.queue_management}
                    onChange={(queue_management) => patch("arr", { queue_management })}
                  />
                </SettingsPanel>
              </Section>
            )}

            {category.slug === "health" && (
              <Section category={category}>
                <SettingsPanel title="Checks">
                  <Toggle
                    id="health-enabled"
                    label={label("Routine checks", "health.enabled")}
                    hint="Checks every file on a schedule that slows as it ages. Re-checks you start and problems found during playback are handled either way."
                    disabled={locked("health.enabled")}
                    checked={draft.health.enabled}
                    onChange={(enabled) => patch("health", { enabled })}
                  />
                  <Field
                    id="health-percent"
                    layout="row"
                    label={
                      <span className="flex w-full justify-between">
                        {label("Sample size", "health.check_percent")}
                        <span className="font-mono tabular-nums">
                          {draft.health.check_percent === 100 ? "Every segment" : `${draft.health.check_percent}%`}
                        </span>
                      </span>
                    }
                    hint="Applies to imports and subsequent health checks. At 100%, every article is checked for availability; the entire file is not downloaded and verified."
                    error={invalid("health.check_percent")}
                  >
                    <Slider
                      id="health-percent"
                      aria-label="Sample size"
                      min={5}
                      max={100}
                      step={5}
                      disabled={locked("health.check_percent")}
                      value={[draft.health.check_percent]}
                      onValueChange={([check_percent]) => patch("health", { check_percent })}
                    />
                  </Field>
                  <Toggle
                    id="health-media"
                    label={label(
                      "Analyze media automatically",
                      "health.media_probe",
                      "ffprobe reads each container once, only while nothing is streaming or importing. The Docker image includes ffprobe.",
                    )}
                    hint="Lists codecs, resolution and audio tracks once a file passes its first check."
                    disabled={locked("health.media_probe")}
                    checked={draft.health.media_probe}
                    onChange={(media_probe) => patch("health", { media_probe })}
                  />
                </SettingsPanel>
                <SettingsPanel title="Repair">
                  <Toggle
                    id="health-repair"
                    label={label("Replace broken files automatically", "health.auto_repair")}
                    hint="Asks the Arr that imported a broken file to find another release. When off, broken files wait in Needs attention."
                    disabled={locked("health.auto_repair")}
                    checked={draft.health.auto_repair}
                    onChange={(auto_repair) => patch("health", { auto_repair })}
                  />
                  {draft.health.auto_repair && (
                    <Field
                      id="health-unowned"
                      layout="row"
                      label={label("Broken files not imported by an Arr", "health.unowned_action")}
                      hint="Files added by hand or by an Arr that is no longer configured cannot be replaced."
                    >
                      <Select
                        disabled={locked("health.unowned_action")}
                        value={draft.health.unowned_action}
                        onValueChange={(v) => patch("health", { unowned_action: v as Doc["health"]["unowned_action"] })}
                      >
                        <SelectTrigger id="health-unowned" className="w-full">
                          <SelectValue />
                        </SelectTrigger>
                        <SelectContent>
                          <SelectItem value="delete">Delete them</SelectItem>
                          <SelectItem value="action_needed">Keep them in Needs attention</SelectItem>
                        </SelectContent>
                      </Select>
                    </Field>
                  )}
                  <Toggle
                    id="health-tolerance"
                    label={label("Keep files with small gaps", "health.degraded_tolerance")}
                    hint="A video missing a few small pieces keeps playing, with a brief glitch, instead of being replaced."
                    disabled={locked("health.degraded_tolerance")}
                    checked={draft.health.degraded_tolerance}
                    onChange={(degraded_tolerance) => patch("health", { degraded_tolerance })}
                  />
                </SettingsPanel>
              </Section>
            )}

            {category.slug === "backups" && (
              <BackupsPanel
                title={category.title}
                description={category.description}
                value={draft.maintenance}
                locked={locked}
                onChange={(maintenance) => setDraft((d) => (d ? { ...d, maintenance } : d))}
              />
            )}

            {category.slug === "maintenance" && (
              <Section category={category}>
                <MaintenancePanel
                  value={draft.maintenance}
                  locked={locked}
                  onChange={(maintenance) => setDraft((d) => (d ? { ...d, maintenance } : d))}
                />
              </Section>
            )}

            {category.slug === "experiments" && (
              <Section category={category}>
                <Alert>
                  <TriangleAlert className="text-warning-text" />
                  <AlertDescription>
                    An experiment can make playback slower as well as faster. Each applies after a restart; turn it off again if
                    anything gets worse.
                  </AlertDescription>
                </Alert>
                <SettingsPanel>
                  {view.experiments.map((e) => {
                    const forced = view.env_experiments.includes(e.name);
                    return (
                      <Toggle
                        key={e.name}
                        id={`experiment-${e.name}`}
                        label={
                          <>
                            {e.title}
                            <Locked locked={forced || locked("experiments")} />
                          </>
                        }
                        hint={e.description}
                        disabled={forced || locked("experiments")}
                        checked={forced || draft.experiments.includes(e.name)}
                        onChange={(on) =>
                          setDraft((d) =>
                            d
                              ? {
                                  ...d,
                                  // Catalog order, so switching one off and on again leaves nothing to save.
                                  experiments: view.experiments
                                    .map((x) => x.name)
                                    .filter((n) => (n === e.name ? on : d.experiments.includes(n))),
                                }
                              : d,
                          )
                        }
                      />
                    );
                  })}
                </SettingsPanel>
              </Section>
            )}

            {category.slug === "webdav" && (
              <Section category={category}>
                <SettingsPanel title="Connection">
                  <CopyField id="dav-address" label="Address" value={address.href} hint={addressHint} />
                  <Field id="dav-user" layout="row" label={label("Username", "webdav.user")} error={invalid("webdav.user")}>
                    <Input
                      id="dav-user"
                      required
                      autoComplete="off"
                      disabled={locked("webdav.user")}
                      value={draft.webdav.user}
                      onChange={(e) => patch("webdav", { user: e.target.value })}
                    />
                  </Field>
                  <SecretField
                    id="dav-password"
                    label="Password"
                    value={draft.webdav.password}
                    locked={locked("webdav.password")}
                    onChange={(password) => patch("webdav", { password })}
                  />
                </SettingsPanel>
                <SettingsPanel title="Download links">
                  <SecretField
                    id="dav-download-key"
                    label="Download key"
                    value={draft.webdav.download_key}
                    locked={locked("webdav.download_key")}
                    generate
                    reveal="webdav.download_key"
                    hint="Lets /view links play without a login."
                    onChange={(download_key) => patch("webdav", { download_key })}
                  />
                </SettingsPanel>
              </Section>
            )}

            {category.slug === "mount" && (
              <Section category={category}>
                <SettingsPanel>
                  {status && (
                    <div className="space-y-0.5">
                      <Status tone={!status.mount.enabled ? "muted" : status.mount.mounted ? "success" : "warning"}>
                        <span className="font-medium">
                          {!status.mount.enabled ? "Built-in FUSE mount off" : status.mount.mounted ? "Mounted" : "Not mounted"}
                        </span>
                      </Status>
                      {status.mount.reason && <p className="text-muted-foreground text-xs">{status.mount.reason}</p>}
                    </div>
                  )}
                  <Toggle
                    id="mount-fuse"
                    label={label("Built-in FUSE mount", "mount.fuse")}
                    hint="Turn off if you do not use the built-in FUSE mount. Takes effect after a restart."
                    disabled={locked("mount.fuse")}
                    checked={draft.mount.fuse}
                    onChange={(fuse) => patch("mount", { fuse })}
                  />
                  <Field
                    id="mount-dir"
                    label={label("Directory", "mount.dir")}
                    hint="An absolute path. Takes effect after a restart; symlinks in your library folders are rewritten then."
                    error={invalid("mount.dir")}
                  >
                    <PreviewedInput
                      id="mount-dir"
                      saved={saved.mount.dir}
                      locked={locked("mount.dir")}
                      onPreview={(mount_dir) => previewChange({ mount_dir })}
                    />
                  </Field>
                  <Toggle
                    id="mount-epoch-times"
                    label={label("Disable file timestamps", "mount.epoch_times")}
                    hint={
                      <>
                        Every file shows a 1970 date. Turn on if your old rclone mount used <code>--no-modtime</code>, before your media server scans, so it does not re-read every file. Takes effect after a restart.
                      </>
                    }
                    disabled={locked("mount.epoch_times")}
                    checked={draft.mount.epoch_times}
                    onChange={(epoch_times) => patch("mount", { epoch_times })}
                  />
                </SettingsPanel>
              </Section>
            )}

            {category.slug === "performance" && (
              <Section category={category}>
                <PerformanceSettings
                  value={draft.performance}
                  locked={locked}
                  invalid={invalid}
                  onChange={(performance) => setDraft((d) => (d ? { ...d, performance } : d))}
                  limit={draft.network.download_limit_mb_per_s ?? null}
                  onLimit={(download_limit_mb_per_s) => patch("network", { download_limit_mb_per_s })}
                />
              </Section>
            )}

            {category.slug === "access" && (
              <Section category={category}>
                <SettingsPanel title="Sign-in">
                  <div className="space-y-3">
                    <Label>{label("Require sign-in", "auth.mode")}</Label>
                    <OptionCards
                      label="Require sign-in"
                      value={draft.auth.mode}
                      disabled={locked("auth.mode")}
                      onChange={(mode) => patch("auth", { mode })}
                      options={[
                        { value: "required", title: "Always", description: "Everyone signs in, wherever they connect from." },
                        {
                          value: "local",
                          title: "Except on the local network",
                          description:
                            "Anyone on your local network can change settings and reveal keys. A reverse proxy must set X-Forwarded-For, or every visitor counts as local.",
                        },
                        {
                          value: "disabled",
                          title: "Never",
                          description: "Anyone who can reach infinidysk can change settings and reveal keys.",
                        },
                      ]}
                    />
                    {draft.auth.mode === "disabled" && (
                      <Alert>
                        <TriangleAlert className="text-warning-text" />
                        <AlertDescription>Only use this behind your own authentication.</AlertDescription>
                      </Alert>
                    )}
                  </div>
                  <Password />
                </SettingsPanel>
                <SettingsPanel title="Single sign-on">
                  <Field
                    id="oidc-display-name"
                    label={label("Display name", "auth.oidc.display_name")}
                    hint="Shown on the sign-in page as Sign in with this name."
                    error={invalid("auth.oidc.display_name")}
                  >
                    <Input
                      id="oidc-display-name"
                      autoComplete="off"
                      placeholder="SSO"
                      disabled={locked("auth.oidc.display_name")}
                      value={draft.auth.oidc.display_name}
                      onChange={(e) => oidc({ display_name: e.target.value })}
                    />
                  </Field>
                  <Field
                    id="oidc-issuer"
                    label={label(
                      "Issuer URL",
                      "auth.oidc.issuer",
                      "Your identity provider's issuer, exactly as it publishes it. Leave empty to turn SSO off.",
                    )}
                    hint="Such as https://auth.example.com/application/o/infinidysk/ or https://keycloak.example.com/realms/media."
                    error={invalid("auth.oidc.issuer")}
                  >
                    <Input
                      id="oidc-issuer"
                      type="url"
                      className="font-mono"
                      disabled={locked("auth.oidc.issuer")}
                      value={draft.auth.oidc.issuer}
                      onChange={(e) => oidc({ issuer: e.target.value })}
                    />
                  </Field>
                  <Field id="oidc-client-id" label={label("Client ID", "auth.oidc.client_id")} error={invalid("auth.oidc.client_id")}>
                    <Input
                      id="oidc-client-id"
                      autoComplete="off"
                      disabled={locked("auth.oidc.client_id")}
                      value={draft.auth.oidc.client_id}
                      onChange={(e) => oidc({ client_id: e.target.value })}
                    />
                  </Field>
                  <SecretField
                    id="oidc-client-secret"
                    label="Client secret"
                    value={draft.auth.oidc.client_secret}
                    locked={locked("auth.oidc.client_secret")}
                    noAutofill
                    error={invalid("auth.oidc.client_secret")}
                    onChange={(client_secret) => oidc({ client_secret })}
                  />
                  <Field
                    id="oidc-redirect-uri"
                    label={label("Redirect URI", "auth.oidc.redirect_uri")}
                    hint={
                      <>
                        Register exactly this address at your identity provider. Empty uses <code>{callback}</code> when the server
                        sees the same address; behind a TLS proxy, set the public URL or list the proxy under trusted proxies.
                      </>
                    }
                    error={invalid("auth.oidc.redirect_uri")}
                  >
                    <Input
                      id="oidc-redirect-uri"
                      type="url"
                      className="font-mono"
                      placeholder={callback}
                      disabled={locked("auth.oidc.redirect_uri")}
                      value={draft.auth.oidc.redirect_uri}
                      onChange={(e) => oidc({ redirect_uri: e.target.value })}
                    />
                  </Field>
                  <Field id="oidc-scopes" label={label("Scopes", "auth.oidc.scopes")} error={invalid("auth.oidc.scopes")}>
                    <Input
                      id="oidc-scopes"
                      className="font-mono"
                      placeholder="openid profile email"
                      disabled={locked("auth.oidc.scopes")}
                      value={draft.auth.oidc.scopes}
                      onChange={(e) => oidc({ scopes: e.target.value })}
                    />
                  </Field>
                  <Field
                    id="oidc-username-claim"
                    label={label("Username claim", "auth.oidc.username_claim")}
                    hint="Falls back to preferred_username, email, then sub."
                    error={invalid("auth.oidc.username_claim")}
                  >
                    <Input
                      id="oidc-username-claim"
                      className="font-mono"
                      disabled={locked("auth.oidc.username_claim")}
                      value={draft.auth.oidc.username_claim}
                      onChange={(e) => oidc({ username_claim: e.target.value })}
                    />
                  </Field>
                  {auth.data?.account && (
                    <div className="space-y-2">
                      <Toggle
                        id="local-login"
                        label={label("Password sign-in", "auth.local_login")}
                        hint={
                          !ssoSaved
                            ? "Always on while SSO is off."
                            : saved.auth.local_login && draft.auth.local_login && !proven
                              ? "To turn this off, first sign in with SSO using the saved settings."
                              : "Off: only SSO signs in. If your identity provider is down, set INFINIDYSK__AUTH__LOCAL_LOGIN=true and restart."
                        }
                        checked={draft.auth.local_login || !ssoSaved}
                        disabled={
                          locked("auth.local_login") || !ssoSaved || (saved.auth.local_login && draft.auth.local_login && !proven)
                        }
                        onChange={(local_login) => patch("auth", { local_login })}
                      />
                      {passwordOffRefusal && <p className="text-destructive text-xs">{passwordOffRefusal}</p>}
                    </div>
                  )}
                </SettingsPanel>
                <SettingsPanel title="Admin API">
                  <SecretField
                    id="admin-key"
                    label="API key"
                    value={draft.auth.api_key}
                    locked={locked("auth.api_key")}
                    generate
                    reveal="auth.api_key"
                    hint="For scripts and scrapers calling the admin API."
                    onChange={(api_key) => patch("auth", { api_key })}
                  />
                  <Toggle
                    id="prometheus"
                    label={label("Prometheus metrics", "metrics.prometheus")}
                    hint={
                      <>
                        Serves <code>/metrics</code>. Scrapers send <code>Authorization: Bearer</code> with the admin API key; scripts
                        can also use an <code>X-Api-Key</code> header.
                      </>
                    }
                    disabled={locked("metrics.prometheus")}
                    checked={draft.metrics.prometheus}
                    onChange={(prometheus) => patch("metrics", { prometheus })}
                  />
                </SettingsPanel>
              </Section>
            )}

            {category.slug === "network" && (
              <Section category={category}>
                <SettingsPanel>
                  <Field
                    id="public-url"
                    label={label(
                      "Public URL",
                      "network.public_url",
                      "Used for Arr webhooks and in STRM playback links. When empty, webhooks use the address each Arr already uses for its SABnzbd client.",
                    )}
                    hint="The address Arrs and browsers use to reach infinidysk, such as your HTTPS reverse-proxy address. STRM files need it set."
                    error={invalid("network.public_url")}
                  >
                    {saved.imports.strategy === "strm" ? (
                      <PreviewedInput
                        id="public-url"
                        saved={saved.network.public_url}
                        locked={locked("network.public_url")}
                        placeholder="https://infinidysk.example.com"
                        onPreview={(public_url) => previewChange({ public_url })}
                      />
                    ) : (
                      <Input
                        id="public-url"
                        type="url"
                        className="font-mono"
                        placeholder="https://infinidysk.example.com"
                        pattern="https?://.+"
                        disabled={locked("network.public_url")}
                        value={draft.network.public_url}
                        onChange={(e) => patch("network", { public_url: e.target.value })}
                      />
                    )}
                  </Field>
                  <Field
                    label={label(
                      "Trusted proxies",
                      "network.trusted_proxies",
                      "The client it names is used for read history and local-network sign-in. Every local range is trusted by default; make sure your reverse proxy sets X-Forwarded-For.",
                    )}
                    hint={
                      <>
                        Proxies whose <code>X-Forwarded-For</code> header is believed. Addresses or CIDR ranges, such as{" "}
                        <code>172.18.0.0/16</code>.
                      </>
                    }
                    error={invalid("network.trusted_proxies")}
                  >
                    <TextList
                      value={draft.network.trusted_proxies}
                      locked={locked("network.trusted_proxies")}
                      onChange={(trusted_proxies) => patch("network", { trusted_proxies })}
                      item="Trusted proxy"
                      placeholder="172.18.0.0/16"
                      empty="None. Clients are shown by the address that connected."
                    />
                  </Field>
                </SettingsPanel>
              </Section>
            )}
          </form>
        </div>
      </div>
      <PreviewDialog request={preview} onClose={() => setPreview(null)} />
      <DiscardDialog
        open={blocker.state === "blocked"}
        description={`Your unsaved changes to ${category.title} will be lost.`}
        onKeep={() => blocker.reset?.()}
        onDiscard={() => {
          reset(view.settings);
          save.reset();
          blocker.proceed?.();
        }}
      />

      {(dirty || save.isError) && (
        <div className="bg-background/90 sticky bottom-0 -mx-4 mt-8 flex items-center justify-end gap-2 border-t px-4 py-3 backdrop-blur lg:-mx-8 lg:px-8">
          {save.isError || (refused && dirty) ? (
            <p role="alert" className="text-destructive mr-auto flex items-start gap-2">
              <CircleAlert className="mt-0.5 size-4 shrink-0" />
              {save.isError ? save.error.message : `Save or discard your changes first, then ${previewTitle(refused!).toLowerCase()}.`}
            </p>
          ) : (
            <span className="text-muted-foreground mr-auto">You have unsaved changes.</span>
          )}
          <Button
            type="button"
            variant="outline"
            disabled={!dirty || save.isPending}
            onClick={() => {
              reset(view.settings);
              save.reset();
            }}
          >
            Discard
          </Button>
          <Button form="settings" disabled={!dirty || save.isPending}>
            {save.isPending && <Loader2 className="animate-spin" />}
            {save.isPending ? "Saving changes…" : "Save changes"}
          </Button>
        </div>
      )}
    </>
  );
}

function TextList({
  value,
  locked,
  onChange,
  item,
  placeholder,
  empty,
}: {
  value: string[];
  locked: boolean;
  onChange: (value: string[]) => void;
  item: string;
  placeholder: string;
  empty: string;
}) {
  return (
    <div className="space-y-2">
      {value.map((entry, i) => (
        <div key={i} className="flex gap-2">
          <Input
            aria-label={`${item} ${i + 1}`}
            className="font-mono"
            placeholder={placeholder}
            spellCheck={false}
            autoCapitalize="off"
            disabled={locked}
            value={entry}
            onChange={(e) => onChange(value.map((p, j) => (j === i ? e.target.value : p)))}
          />
          {!locked && (
            <IconButton size="icon" label={`Remove ${item.toLowerCase()} ${i + 1}`} onClick={() => onChange(value.filter((_, j) => j !== i))}>
              <Trash2 />
            </IconButton>
          )}
        </div>
      ))}
      {value.length === 0 && <p className="text-muted-foreground">{empty}</p>}
      {!locked && (
        <Button type="button" variant="outline" size="sm" onClick={() => onChange([...value, ""])}>
          <Plus />
          Add {item.split(" ").pop()?.toLowerCase()}
        </Button>
      )}
    </div>
  );
}

function Password() {
  const client = useQueryClient();
  const auth = useAuth();
  const [open, setOpen] = useState(false);
  const [current, setCurrent] = useState("");
  const [password, setPassword] = useState("");
  const change = useMutation({
    mutationFn: () => body(api.PUT("/api/v1/auth/password", { body: { current_password: current, password } })),
    onSuccess: () => {
      close();
      toast.success("Password changed");
      void client.invalidateQueries({ queryKey: ["auth"] });
    },
  });
  const close = () => {
    setOpen(false);
    setCurrent("");
    setPassword("");
    change.reset();
  };
  const guard = useDiscardGuard(!!(current || password), close);
  if (!auth.data?.account) {
    return (
      <p className="text-muted-foreground text-xs">
        {auth.data?.sso
          ? "No admin account; sign-in uses SSO. If your identity provider is down, set INFINIDYSK__AUTH__MODE=local and restart, then open infinidysk from your local network."
          : "No admin account exists yet. When sign-in is required, the next visit asks you to create one."}
      </p>
    );
  }
  return (
    <div className="flex items-start justify-between gap-4">
      <div>
        <p className="text-sm font-medium">Password</p>
        <p className="text-muted-foreground mt-1 text-xs">Changing it signs out every other password session.</p>
      </div>
      <Button type="button" variant="outline" onClick={() => setOpen(true)}>
        Change password
      </Button>
      <Dialog open={open} onOpenChange={(next) => !next && guard.requestClose()}>
        <DialogContent>
          {/* The dialog renders inside the settings form in React's tree; stop the submit from reaching it. */}
          <form
            className="grid gap-4"
            onSubmit={(e) => {
              e.preventDefault();
              e.stopPropagation();
              change.mutate();
            }}
          >
            <DialogHeader>
              <DialogTitle>Change password</DialogTitle>
              <DialogDescription>Every other session is signed out.</DialogDescription>
            </DialogHeader>
            <Field id="current-password" label="Current password">
              <Input
                id="current-password"
                type="password"
                autoComplete="current-password"
                required
                value={current}
                onChange={(e) => setCurrent(e.target.value)}
              />
            </Field>
            <Field id="new-password" label="New password" hint="At least 8 characters.">
              <Input
                id="new-password"
                type="password"
                autoComplete="new-password"
                minLength={8}
                required
                value={password}
                onChange={(e) => setPassword(e.target.value)}
              />
            </Field>
            {change.isError && <Failure error={change.error} />}
            <DialogFooter>
              <Button type="button" variant="outline" onClick={guard.requestClose}>
                Cancel
              </Button>
              <Button disabled={change.isPending}>
                {change.isPending && <Loader2 className="animate-spin" />}
                Change password
              </Button>
            </DialogFooter>
          </form>
        </DialogContent>
      </Dialog>
      {guard.dialog}
    </div>
  );
}
