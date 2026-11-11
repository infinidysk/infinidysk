import { useEffect, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { CircleAlert, Info, Loader2, Pencil, PlugZap, Plus, RefreshCw, Trash2, TriangleAlert } from "lucide-react";
import { api, body, type Schemas } from "../api/client";
import { ago } from "../format";
import { MASK, notAPassword, useSaveSettings, useSettings, uuid } from "../settings";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import {
  ConfirmButton,
  EditorSheet,
  Empty,
  Failure,
  Field,
  FieldGroup,
  IconButton,
  InfoTip,
  Loading,
  SettingsSection,
  Status,
  Toggle,
} from "../ui";

type Arr = Schemas["ArrInstance"];
type ArrStatus = Schemas["ArrInstanceStatus"];

const profiles: Schemas["ArrProfile"][] = ["sonarr", "radarr", "lidarr", "chaptarr", "whisparr", "sportarr"];
const ports: Record<Schemas["ArrProfile"], number> = {
  sonarr: 8989,
  radarr: 7878,
  lidarr: 8686,
  chaptarr: 8789,
  whisparr: 6969,
  sportarr: 1867,
};
const typeName = (p: Schemas["ArrProfile"]) => p[0]!.toUpperCase() + p.slice(1);

/** The Arr list; each change saves at once, like Providers. */
export function ArrInstances({ title, description }: { title: string; description: string }) {
  const settings = useSettings();
  const save = useSaveSettings();
  const [editing, setEditing] = useState<Arr | null>(null);
  const status = useQuery({
    queryKey: ["arr-status"],
    queryFn: () => body(api.GET("/api/v1/arr/status")),
    refetchInterval: 5000,
  }).data;

  if (settings.isPending || settings.isError)
    return (
      <SettingsSection title={title} description={description}>
        {settings.isError ? <Failure error={settings.error} /> : <Loading />}
      </SettingsSection>
    );

  const view = settings.data;
  const locked = view.locked.some((l) => l === "arr_instances" || l.startsWith("arr_instances."));
  const arr = view.settings.arr_instances;
  const existing = editing !== null && arr.some((a) => a.id === editing.id);
  const store = (next: Arr[], after?: () => void) =>
    save.mutate({ ...view.settings, arr_instances: next }, { onSuccess: after });
  const add = () => {
    save.reset();
    setEditing({ id: uuid(), name: "", profile: "sonarr", url: "", api_key: "", enabled: true });
  };
  const addButton = (variant: "default" | "outline") => (
    <Button variant={variant} disabled={locked} onClick={add}>
      <Plus />
      Add Arr instance
    </Button>
  );

  return (
    <SettingsSection title={title} description={description} actions={addButton("default")}>
      {locked && (
        <Alert>
          <Info />
          <AlertDescription>Arr instances are set by environment variables and cannot be changed here.</AlertDescription>
        </Alert>
      )}
      {save.isError && editing === null && <Failure error={save.error} />}
      {arr.length === 0 ? (
        <Empty title="No Arr instances" action={!locked && addButton("outline")}>
          Add Sonarr, Radarr or another Arr so broken files can be replaced.
        </Empty>
      ) : (
        <ul className="bg-card divide-y rounded-lg border">
          {arr.map((a) => (
            <li key={a.id} className="flex items-start gap-3 px-3 py-2.5">
              <div className={a.enabled === false ? "min-w-0 flex-1 opacity-60" : "min-w-0 flex-1"}>
                <div className="flex items-center gap-2">
                  <span className="truncate font-medium">{a.name}</span>
                  <Badge variant="secondary">{typeName(a.profile)}</Badge>
                  {a.enabled === false && <Badge variant="outline">Disabled</Badge>}
                </div>
                <div className="text-muted-foreground truncate font-mono text-xs">{a.url}</div>
                {a.enabled !== false && <Summary status={status?.instances.find((s) => s.id === a.id)} />}
              </div>
              <div className="flex shrink-0 gap-0.5 max-sm:[&_button]:size-9">
                <IconButton
                  label={locked ? `View ${a.name}` : `Edit ${a.name}`}
                  onClick={() => {
                    save.reset();
                    setEditing(a);
                  }}
                >
                  <Pencil />
                </IconButton>
                {!locked && (
                  <ConfirmButton
                    icon={<Trash2 />}
                    label={`Remove ${a.name}`}
                    title={`Remove ${a.name}?`}
                    message="It is no longer asked to replace broken files."
                    confirm="Remove"
                    onConfirm={() => store(arr.filter((x) => x.id !== a.id))}
                  />
                )}
              </div>
            </li>
          ))}
        </ul>
      )}
      <ArrSheet
        arr={editing}
        existing={existing}
        locked={locked}
        status={status?.instances.find((s) => s.id === editing?.id)}
        error={editing ? save.error : null}
        saving={save.isPending}
        onSave={(a) => store(existing ? arr.map((x) => (x.id === a.id ? a : x)) : [...arr, a], () => setEditing(null))}
        onClose={() => setEditing(null)}
      />
    </SettingsSection>
  );
}

/** One line on what discovery last found: connection, SABnzbd clients pointing here, webhook trouble. */
function Summary({ status }: { status?: ArrStatus }) {
  if (!status) return null;
  if (status.checked_at_ms == null)
    return (
      <Status tone="muted" className="text-xs">
        Checking…
      </Status>
    );
  if (status.error)
    return (
      <div className="flex min-w-0 items-center gap-2 text-xs">
        <Status tone="destructive">Not connected</Status>
        <span className="text-muted-foreground truncate">{status.error}</span>
        <Retry size="xs" />
      </div>
    );
  const here = status.clients.filter((c) => c.binding === "verified").length;
  const webhook = status.webhook_error ? "webhook not registered" : status.delivery_error ? "last webhook delivery failed" : null;
  const issue = webhook ?? (status.warnings.length > 0 ? "a setting needs changing" : null);
  return (
    <div className="flex min-w-0 items-start gap-2 text-xs">
      <Status tone={issue || here === 0 ? "warning" : "success"}>Connected</Status>
      <span className="text-muted-foreground min-w-0 text-pretty">
        {here === 0 ? "add infinidysk as a SABnzbd download client in this Arr" : `sends to infinidysk through ${here} download client${here === 1 ? "" : "s"}`}
        {issue && ` · ${issue}`}
      </span>
    </div>
  );
}

/** Checks every Arr again now, instead of waiting for the next automatic retry. */
function Retry({ size }: { size: "xs" | "sm" }) {
  const client = useQueryClient();
  const retry = useMutation({
    mutationFn: () => body(api.POST("/api/v1/arr/discover")),
    // Discovery runs in the background; give it a moment before reading the result.
    onSuccess: () => setTimeout(() => void client.invalidateQueries({ queryKey: ["arr-status"] }), 1500),
  });
  return (
    <>
      <Button type="button" variant="outline" size={size} className="shrink-0" disabled={retry.isPending} onClick={() => retry.mutate()}>
        {retry.isPending ? <Loader2 className="animate-spin" /> : <RefreshCw />}
        Retry
      </Button>
      {retry.isError && (
        <span role="alert" className="text-destructive text-xs">
          {retry.error.message}
        </span>
      )}
    </>
  );
}

/** Add, edit or (when locked) view one Arr instance. */
function ArrSheet({
  arr,
  existing,
  locked,
  status,
  error,
  saving,
  onSave,
  onClose,
}: {
  arr: Arr | null;
  existing: boolean;
  locked: boolean;
  status?: ArrStatus;
  error: Error | null;
  saving: boolean;
  onSave: (arr: Arr) => void;
  onClose: () => void;
}) {
  const [draft, setDraft] = useState<Arr | null>(arr);
  useEffect(() => setDraft(arr), [arr]);
  const set = (patch: Partial<Arr>) => setDraft((d) => (d ? { ...d, ...patch } : d));
  const isNew = arr !== null && !existing;

  return (
    <EditorSheet
      open={arr !== null}
      title={isNew ? "Add Arr instance" : `${locked ? "" : "Edit "}${arr?.name}`}
      description={locked ? "Set by environment variables." : "Asked for a replacement when one of its files breaks."}
      dirty={JSON.stringify(draft) !== JSON.stringify(arr)}
      saving={saving}
      error={error}
      submit={isNew ? "Add Arr instance" : "Save changes"}
      readOnly={locked}
      onSubmit={() => draft && !locked && onSave(draft)}
      onClose={onClose}
    >
      {draft && (
        <>
          <fieldset disabled={locked} className="space-y-4">
            <Field id="arr-type" label="Type" layout="horizontal">
              <Select value={draft.profile} onValueChange={(profile) => set({ profile: profile as Arr["profile"] })}>
                <SelectTrigger id="arr-type" className="w-full">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {profiles.map((p) => (
                    <SelectItem key={p} value={p}>
                      {typeName(p)}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </Field>
            <Field id="arr-name" label="Name" layout="horizontal">
              <Input id="arr-name" required value={draft.name} onChange={(e) => set({ name: e.target.value })} />
            </Field>
            <Field id="arr-url" label="URL" layout="horizontal">
              <Input
                id="arr-url"
                type="url"
                className="font-mono"
                placeholder={`http://${draft.profile}:${ports[draft.profile]}`}
                required
                value={draft.url}
                onChange={(e) => set({ url: e.target.value.trim() })}
              />
            </Field>
            <Field id="arr-key" label="API key" layout="horizontal">
              <Input
                id="arr-key"
                type="password"
                className="font-mono"
                {...notAPassword}
                placeholder={draft.api_key === MASK ? "••••••••" : undefined}
                required={draft.api_key !== MASK}
                value={draft.api_key === MASK ? "" : draft.api_key}
                onChange={(e) => set({ api_key: e.target.value || (existing && arr?.api_key === MASK ? MASK : "") })}
              />
            </Field>
            <Toggle
              id="arr-enabled"
              label="Enabled"
              disabled={locked}
              checked={draft.enabled ?? true}
              onChange={(enabled) => set({ enabled })}
            />
          </fieldset>
          <ArrTest key={`${draft.profile}|${draft.url}|${draft.api_key}`} arr={draft} />
          {existing && draft.enabled !== false && status && (
            <FieldGroup title="Discovered">
              <Discovered status={status} />
            </FieldGroup>
          )}
        </>
      )}
    </EditorSheet>
  );
}

// Remounted (keyed) when the type, URL or key changes, so a stale result never shows.
function ArrTest({ arr }: { arr: Arr }) {
  const test = useMutation({ mutationFn: () => body(api.POST("/api/v1/arr/test", { body: arr })) });
  const result = test.data;
  const failed = test.error?.message ?? result?.error;
  return (
    <div className="flex flex-wrap items-center gap-3">
      <Button
        type="button"
        variant="outline"
        disabled={test.isPending || !arr.url || !arr.api_key}
        onClick={() => test.mutate()}
      >
        {test.isPending ? <Loader2 className="animate-spin" /> : <PlugZap />}
        Test connection
      </Button>
      {failed ? (
        <p className="text-destructive flex items-start gap-2">
          <CircleAlert className="mt-0.5 size-4 shrink-0" />
          {failed}
        </p>
      ) : (
        result && (
          <Status tone="success">
            Connected<span className="text-muted-foreground font-mono">v{result.version}</span>
          </Status>
        )
      )}
    </div>
  );
}

const bindings: Record<Schemas["ArrBinding"], { label: string; variant: "secondary" | "outline"; hint: string }> = {
  verified: { label: "This install", variant: "secondary", hint: "Confirmed: this client sends its downloads here." },
  unverified: {
    label: "Unconfirmed",
    variant: "outline",
    hint: "Its API key is hidden and this Arr cannot test it, so it is not used to route imports.",
  },
  other: { label: "Another server", variant: "outline", hint: "Its API key belongs to a different SABnzbd." },
};

// What discovery last found for one saved Arr: its SABnzbd clients and which point here.
function Discovered({ status }: { status: ArrStatus }) {
  if (status.checked_at_ms == null) {
    return (
      <p className="text-muted-foreground flex items-center gap-2">
        <Loader2 className="size-4 animate-spin" />
        Looking for download clients…
      </p>
    );
  }
  return (
    <div className="space-y-2">
      <p className="flex flex-wrap items-center gap-x-2">
        <Status tone={status.error ? "destructive" : "success"}>{status.error ? "Not connected" : "Connected"}</Status>
        {status.version && <span className="text-muted-foreground font-mono">v{status.version}</span>}
        <span className="text-muted-foreground">· checked {ago(status.checked_at_ms)}</span>
      </p>
      {status.error && (
        <div className="flex flex-wrap items-start justify-between gap-2">
          <p className="text-destructive flex items-start gap-2">
            <CircleAlert className="mt-0.5 size-4 shrink-0" />
            {status.error}
          </p>
          <Retry size="sm" />
        </div>
      )}
      {(!status.error || status.clients.length > 0) && <p className="text-muted-foreground">SABnzbd download clients</p>}
      {status.clients.length === 0 && !status.error && <p>None. Add this install to the Arr as a SABnzbd download client.</p>}
      {status.clients.length > 0 && (
        <ul className="divide-y rounded-md border">
          {status.clients.map((c) => (
            <li key={c.name} className="flex items-center justify-between gap-2 px-3 py-2">
              <span className="min-w-0 break-words">
                {c.name}
                <span className="text-muted-foreground ml-2 font-mono break-all">{c.categories.join(", ") || "no category"}</span>
              </span>
              <span className="flex shrink-0 items-center gap-1.5">
                <Badge variant={bindings[c.binding].variant}>{bindings[c.binding].label}</Badge>
                <InfoTip label={`About ${bindings[c.binding].label}`}>{bindings[c.binding].hint}</InfoTip>
              </span>
            </li>
          ))}
        </ul>
      )}
      {status.warnings.map((w) => (
        <p key={w} className="flex items-start gap-2 text-warning-text">
          <TriangleAlert className="mt-0.5 size-4 shrink-0" />
          {w}
        </p>
      ))}
      <Webhook status={status} />
    </div>
  );
}

// Our webhook on the Arr; missed deliveries are only a warning since reconciliation covers them.
function Webhook({ status }: { status: ArrStatus }) {
  if (status.webhook_error) {
    return (
      <p className="flex items-start gap-2 text-warning-text">
        <TriangleAlert className="mt-0.5 size-4 shrink-0" />
        Webhook not registered: {status.webhook_error}
      </p>
    );
  }
  if (!status.webhook_url) return null;
  return (
    <div className="text-muted-foreground space-y-1">
      <p className="truncate" title={status.webhook_url}>
        Webhook <span className="font-mono">{status.webhook_url}</span>
      </p>
      {status.delivery_error ? (
        <p className="flex items-start gap-2 text-warning-text">
          <TriangleAlert className="mt-0.5 size-4 shrink-0" />
          Last delivery failed: {status.delivery_error}
        </p>
      ) : (
        <p>{status.delivery_at_ms != null ? `Last delivery ${ago(status.delivery_at_ms)}` : "No deliveries since start"}</p>
      )}
    </div>
  );
}
