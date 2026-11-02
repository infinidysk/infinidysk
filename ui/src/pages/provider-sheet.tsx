import { useEffect, useState } from "react";
import { useMutation } from "@tanstack/react-query";
import { CircleAlert, Loader2, PlugZap } from "lucide-react";
import { api, body, type Schemas } from "../api/client";
import { MASK, uuid } from "../settings";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Select, SelectContent, SelectItem, SelectSeparator, SelectTrigger, SelectValue } from "@/components/ui/select";
import { AdvancedSettings, EditorSheet, Field, FieldGroup, Status, Toggle } from "../ui";

type Provider = Schemas["Provider"];
type Catalog = Schemas["Catalog"];
type Quota = Schemas["Quota"];

/** Select values cannot be empty; these stand in for "no profile" and "no backbone". */
const CUSTOM = "__custom";
const NONE = "__none";

/** The fields a login check depends on. */
const loginOf = (p: Provider) => JSON.stringify([p.profile, p.hostname, p.port, p.tls, p.username, p.password]);

export function blankProvider(priority: number): Provider {
  return {
    id: uuid(),
    name: "",
    profile: null,
    hostname: "",
    port: 563,
    tls: true,
    username: "",
    password: "",
    max_connections: 10,
    pipeline_depth: 4,
    priority,
    backbone: null,
    enabled: true,
    quota: null,
  };
}

/** Add or edit one provider. Catalog providers take hostname, port and backbone from the profile. */
export function ProviderSheet({
  provider,
  existing,
  catalog,
  error,
  saving,
  onSave,
  onClose,
}: {
  provider: Provider | null;
  existing: boolean;
  catalog: Catalog;
  error: Error | null;
  saving: boolean;
  onSave: (provider: Provider) => void;
  onClose: () => void;
}) {
  const [draft, setDraft] = useState<Provider | null>(provider);
  const check = useMutation({ mutationFn: (p: Provider) => body(api.POST("/api/v1/providers/check", { body: p })) });
  const [blocked, setBlocked] = useState<string | null>(null);
  // A new provider starts with nothing picked, so the catalog comes before a custom server.
  const [picked, setPicked] = useState(existing);
  const { reset } = check;
  useEffect(() => {
    setDraft(provider);
    setBlocked(null);
    setPicked(existing);
    reset();
  }, [provider, existing, reset]);

  const set = (patch: Partial<Provider>) => setDraft((d) => (d ? { ...d, ...patch } : d));
  const profile = catalog.profiles.find((p) => p.id === draft?.profile);
  const server = profile?.servers.find((s) => s.hostname === draft?.hostname);
  const backbone = profile ? server?.backbone : draft?.backbone;
  const backboneName = catalog.backbones.find((b) => b.id === backbone)?.name;
  const isNew = provider !== null && !existing;
  const dirty = JSON.stringify(draft) !== JSON.stringify(provider);

  // A result for other login fields is stale.
  const current = draft && check.variables && loginOf(check.variables) === loginOf(draft) ? check : null;
  const failed = current?.error?.message ?? current?.data?.error;
  const showBlocked = !!draft && !!failed && blocked === loginOf(draft);

  const submit = () => {
    if (!draft) return;
    const changed = isNew || !provider || loginOf(draft) !== loginOf(provider);
    const verified = current?.isSuccess && !current.data.error;
    if (!draft.enabled || !changed || verified) return onSave(draft);
    setBlocked(loginOf(draft));
    check.mutate(draft, { onSuccess: (r) => !r.error && onSave(draft) });
  };

  return (
    <EditorSheet
      open={provider !== null}
      title={isNew ? "Add provider" : `Edit ${provider?.name}`}
      description={isNew ? "Pick your provider, or enter a custom server." : "Changes apply to new connections right away."}
      dirty={dirty}
      saving={saving || check.isPending}
      error={error}
      notice={
        showBlocked && (
          <Alert variant="destructive">
            <CircleAlert />
            <AlertDescription>
              <p>Could not log in with these settings. {failed}</p>
              <Button type="button" variant="outline" size="sm" className="mt-2" onClick={() => draft && onSave(draft)}>
                {isNew ? "Add anyway" : "Save anyway"}
              </Button>
            </AlertDescription>
          </Alert>
        )
      }
      submit={isNew ? "Add provider" : "Save changes"}
      onSubmit={submit}
      onClose={onClose}
    >
      {draft && (
        <>
          <FieldGroup title="Server">
            <Field id="profile" label="Provider" layout="horizontal">
              <Select
                required
                value={picked ? (draft.profile ?? CUSTOM) : ""}
                onValueChange={(value) => {
                  const next = catalog.profiles.find((p) => p.id === value);
                  setPicked(true);
                  set(
                    next
                      ? { profile: next.id, hostname: next.servers[0]?.hostname ?? "", port: next.servers[0]?.port ?? 563, tls: true, backbone: null }
                      : { profile: null, hostname: "", backbone: null },
                  );
                }}
              >
                <SelectTrigger id="profile" className="w-full">
                  <SelectValue placeholder="Pick your provider" />
                </SelectTrigger>
                <SelectContent>
                  {catalog.profiles.map((p) => (
                    <SelectItem key={p.id} value={p.id}>
                      {p.name}
                    </SelectItem>
                  ))}
                  <SelectSeparator />
                  <SelectItem value={CUSTOM}>Custom server</SelectItem>
                </SelectContent>
              </Select>
              {profile && (
                <div className="flex flex-wrap items-center justify-between gap-x-3 text-xs">
                  <span className="text-muted-foreground font-mono break-all">
                    {draft.hostname}:{draft.port} · TLS{backboneName && ` · ${backboneName}`}
                  </span>
                  <Button
                    type="button"
                    variant="link"
                    size="sm"
                    className="h-auto p-0 text-xs"
                    onClick={() => set({ profile: null, backbone: backbone ?? null })}
                  >
                    Use a custom server
                  </Button>
                </div>
              )}
            </Field>

            {profile && profile.servers.length > 1 && (
              <Field id="server" label="Server" layout="horizontal">
                <Select
                  value={draft.hostname}
                  onValueChange={(hostname) => set({ hostname, port: profile.servers.find((s) => s.hostname === hostname)?.port ?? draft.port })}
                >
                  <SelectTrigger id="server" className="w-full">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {profile.servers.map((s) => (
                      <SelectItem key={s.hostname} value={s.hostname}>
                        {s.hostname}
                        {s.region ? ` (${s.region})` : ""}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </Field>
            )}

            {picked && !profile && (
              <>
                <Field id="hostname" label="Hostname" layout="horizontal">
                  <Input
                    id="hostname"
                    required
                    placeholder="news.example.com"
                    value={draft.hostname}
                    onChange={(e) => set({ hostname: e.target.value.trim() })}
                  />
                </Field>
                <Field id="port" label="Port" layout="horizontal">
                  <Input
                    id="port"
                    type="number"
                    className="w-28"
                    min={1}
                    max={65535}
                    required
                    value={draft.port}
                    onChange={(e) => set({ port: Number(e.target.value) })}
                  />
                </Field>
                <Toggle id="tls" label="Use TLS" checked={draft.tls} onChange={(tls) => set({ tls })} />
                <Field
                  id="backbone"
                  label="Backbone"
                  layout="horizontal"
                  hint="Providers on the same backbone share articles, so a miss on one is not retried on the other."
                >
                  <Select value={backbone ?? NONE} onValueChange={(value) => set({ backbone: value === NONE ? null : value })}>
                    <SelectTrigger id="backbone" className="w-full">
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      <SelectItem value={NONE}>Unknown (its own group)</SelectItem>
                      <SelectSeparator />
                      {catalog.backbones.map((b) => (
                        <SelectItem key={b.id} value={b.id}>
                          {b.name}
                        </SelectItem>
                      ))}
                    </SelectContent>
                  </Select>
                </Field>
              </>
            )}
          </FieldGroup>

          <FieldGroup title="Account">
            <Field id="name" label="Display name" layout="horizontal">
              <Input
                id="name"
                placeholder={profile?.name ?? draft.hostname}
                value={draft.name}
                onChange={(e) => set({ name: e.target.value })}
              />
            </Field>
            <Field id="username" label="Username" layout="horizontal">
              <Input id="username" autoComplete="off" value={draft.username} onChange={(e) => set({ username: e.target.value })} />
            </Field>
            <Field id="password" label="Password" layout="horizontal">
              <Input
                id="password"
                type="password"
                autoComplete="new-password"
                placeholder={draft.password === MASK ? "••••••••" : ""}
                value={draft.password === MASK ? "" : draft.password}
                onChange={(e) => set({ password: e.target.value || (isNew ? "" : MASK) })}
              />
            </Field>
            <div className="flex flex-wrap items-center gap-3">
              <Button
                type="button"
                variant="outline"
                disabled={check.isPending || !draft.hostname}
                onClick={() => {
                  setBlocked(null);
                  check.mutate(draft);
                }}
              >
                {check.isPending ? <Loader2 className="animate-spin" /> : <PlugZap />}
                Test connection
              </Button>
              {current && !current.isPending && !showBlocked &&
                (failed ? (
                  <p className="text-destructive flex items-start gap-2">
                    <CircleAlert className="mt-0.5 size-4 shrink-0" />
                    {failed}
                  </p>
                ) : (
                  <Status tone="success">Logged in</Status>
                ))}
            </div>
          </FieldGroup>

          <FieldGroup title="Connections">
            <Field id="connections" label="Connections" layout="horizontal" hint="Your plan's connection limit.">
              <Input
                id="connections"
                type="number"
                className="w-28"
                min={1}
                max={1000}
                required
                value={draft.max_connections}
                onChange={(e) => set({ max_connections: Number(e.target.value) })}
              />
            </Field>
            <Toggle id="enabled" label="Enabled" checked={draft.enabled} onChange={(enabled) => set({ enabled })} />
          </FieldGroup>

          <FieldGroup title="Quota">
            <Toggle
              id="quota"
              label="Download quota"
              hint={
                draft.quota
                  ? draft.quota.period === "monthly"
                    ? "Usage resets at 00:00 UTC on the reset day; short months reset on their last day. At the cap, other providers take over."
                    : "Usage only resets when you reset it. At the cap, other providers take over."
                  : "For plans with a data cap. Usage is tracked either way."
              }
              checked={!!draft.quota}
              onChange={(on) => set({ quota: on ? { gb: 1000, period: "monthly", reset_day: 1 } : null })}
            />
            {draft.quota && (
              <>
                <Field id="quota-gb" label="Quota (GB)" layout="horizontal">
                  <Input
                    id="quota-gb"
                    type="number"
                    className="w-28"
                    min={1}
                    required
                    value={draft.quota.gb}
                    onChange={(e) => draft.quota && set({ quota: { ...draft.quota, gb: Number(e.target.value) } })}
                  />
                </Field>
                <Field id="quota-period" label="Resets" layout="horizontal">
                  <Select
                    value={draft.quota.period}
                    onValueChange={(period) => draft.quota && set({ quota: { ...draft.quota, period: period as Quota["period"] } })}
                  >
                    <SelectTrigger id="quota-period" className="w-36">
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      <SelectItem value="monthly">Monthly</SelectItem>
                      <SelectItem value="block">Never (block)</SelectItem>
                    </SelectContent>
                  </Select>
                </Field>
                {draft.quota.period === "monthly" && (
                  <Field id="quota-day" label="Reset day" layout="horizontal">
                    <Input
                      id="quota-day"
                      type="number"
                      className="w-28"
                      min={1}
                      max={31}
                      required
                      value={draft.quota.reset_day ?? 1}
                      onChange={(e) => draft.quota && set({ quota: { ...draft.quota, reset_day: Number(e.target.value) } })}
                    />
                  </Field>
                )}
              </>
            )}
          </FieldGroup>

          <AdvancedSettings>
            <Field id="pipeline" label="Pipeline depth" layout="horizontal" hint="Requests per connection; Auto-tune sets it.">
              <Input
                id="pipeline"
                type="number"
                className="w-28"
                min={1}
                max={16}
                required
                value={draft.pipeline_depth}
                onChange={(e) => set({ pipeline_depth: Number(e.target.value) })}
              />
            </Field>
          </AdvancedSettings>
        </>
      )}
    </EditorSheet>
  );
}
