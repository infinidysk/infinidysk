import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Loader2, Pencil, PlugZap, Plus, Trash2 } from "lucide-react";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { ConfirmButton, EditorSheet, Empty, Failure, Field, IconButton, Loading, SettingsSection, Status, Toggle } from "../ui";
import { api, body, type Schemas } from "../api/client";
import { MASK, notAPassword, useSettings } from "../settings";

type Indexer = Schemas["IndexerRead"];
type ConnectionResult = Schemas["IndexerTest"];

export function Indexers({ title, description }: { title: string; description: string }) {
  const settings = useSettings();
  const client = useQueryClient();
  const indexers = useQuery({
    queryKey: ["indexers"],
    queryFn: () => body(api.GET("/api/v1/indexers")),
  });
  const [editing, setEditing] = useState<Indexer | null>(null);
  const [draft, setDraft] = useState<Indexer | null>(null);
  const [checked, setChecked] = useState<{ revision: string; result: ConnectionResult } | null>(null);
  const updated = () => {
    void client.invalidateQueries({ queryKey: ["indexers"] });
    void client.invalidateQueries({ queryKey: ["settings"] });
  };
  const save = useMutation({
    mutationFn: (value: Indexer) =>
      value.id
        ? body(api.PUT("/api/v1/indexers/{id}", { params: { path: { id: value.id } }, body: value }))
        : body(api.POST("/api/v1/indexers", { body: {
            name: value.name,
            base_url: value.base_url,
            api_key: value.api_key,
            enabled: value.enabled,
          } })),
    onSuccess: () => {
      updated();
      setEditing(null);
      setChecked(null);
    },
    onError: updated,
  });
  const remove = useMutation({
    mutationFn: (id: string) => body(api.DELETE("/api/v1/indexers/{id}", { params: { path: { id } } })),
    onSuccess: updated,
  });
  const toggle = useMutation({
    mutationFn: ({ id, enabled }: { id: string; enabled: boolean }) =>
      body(api.PATCH("/api/v1/indexers/{id}/enabled", { params: { path: { id } }, body: { enabled } })),
    onSuccess: updated,
    onError: updated,
  });
  const test = useMutation({
    mutationFn: ({ id }: Indexer) => body(api.POST("/api/v1/indexers/{id}/test", { params: { path: { id } } })),
    onSuccess: (result, { revision }) => setChecked({ revision, result }),
  });
  const managed = settings.data?.locked.some((path) => path === "indexers" || path.startsWith("indexers."));
  const locked = !settings.data || managed === true;
  const list = indexers.data ?? [];
  const origin = (url: string) => {
    try {
      return new URL(url).origin;
    } catch {
      return "";
    }
  };
  const keyRequired = !!(editing?.id && draft && origin(editing.base_url) !== origin(draft.base_url));
  const openEditor = (item: Indexer) => {
    save.reset();
    setEditing(item);
    setDraft(item);
  };
  const add = () => openEditor({ id: "", name: "", base_url: "", api_key: "", enabled: true, revision: "" });
  const addButton = (variant: "default" | "outline") => (
    <Button variant={variant} disabled={locked} onClick={add}>
      <Plus />
      Add indexer
    </Button>
  );

  return (
    <SettingsSection title={title} description={description} actions={addButton("default")}>
      {managed && (
        <Alert>
          <AlertDescription>Indexers are managed by environment variables and cannot be edited here.</AlertDescription>
        </Alert>
      )}
      {indexers.isPending || settings.isPending ? <Loading /> : indexers.isError ? <Failure error={indexers.error} /> : null}
      {settings.isError && <Failure error={settings.error} />}
      {remove.isError && <Failure error={remove.error} />}
      {toggle.isError && <Failure error={toggle.error} />}
      {test.isError && <Failure error={test.error} />}
      {!indexers.isPending && !indexers.isError && (
        list.length === 0 ? (
          <Empty title="No indexers" action={!locked && addButton("outline")}>
            Add a Newznab-compatible indexer to manage its connection.
          </Empty>
        ) : (
          <ul className="bg-card divide-y rounded-lg border">
            {list.map((item) => (
              <li key={item.id} className="flex flex-wrap items-center gap-3 px-3 py-3">
                <div className={item.enabled ? "min-w-0 flex-1" : "min-w-0 flex-1 opacity-60"}>
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="font-medium break-words">{item.name}</span>
                    {!item.enabled && <Badge variant="outline">Disabled</Badge>}
                  </div>
                  <div className="text-muted-foreground break-all font-mono text-xs">{item.base_url}</div>
                  {checked?.revision === item.revision && (
                    <div className="mt-1">
                      <Status tone={checked.result.success ? "success" : "destructive"}>
                        {checked.result.success ? "Connected" : checked.result.error ?? "Not connected"}
                      </Status>
                    </div>
                  )}
                </div>
                <div className="flex flex-wrap items-center gap-2 max-sm:w-full max-sm:justify-between">
                  <Toggle
                    id={`indexer-enabled-${item.id}`}
                    label="Enabled"
                    checked={item.enabled}
                    disabled={locked || toggle.isPending}
                    onChange={(enabled) => toggle.mutate({ id: item.id, enabled })}
                  />
                  <Button
                    type="button"
                    variant="outline"
                    size="sm"
                    disabled={test.isPending}
                    onClick={() => {
                      setChecked(null);
                      test.mutate(item);
                    }}
                  >
                    {test.isPending && test.variables?.id === item.id ? <Loader2 className="animate-spin" /> : <PlugZap />}
                    Test
                  </Button>
                  <IconButton
                    label={locked ? `View ${item.name}` : `Edit ${item.name}`}
                    onClick={() => openEditor(item)}
                  >
                    <Pencil />
                  </IconButton>
                  {!locked && (
                    <ConfirmButton
                      icon={<Trash2 />}
                      label={`Remove ${item.name}`}
                      title={`Remove ${item.name}?`}
                      message="The indexer configuration will be deleted."
                      confirm="Remove"
                      onConfirm={() => remove.mutate(item.id)}
                    />
                  )}
                </div>
              </li>
            ))}
          </ul>
        )
      )}
      <EditorSheet
        open={editing !== null}
        title={editing?.id ? `Edit ${editing.name}` : "Add indexer"}
        description="Newznab API connection settings."
        dirty={JSON.stringify(editing) !== JSON.stringify(draft)}
        saving={save.isPending}
        error={save.error}
        submit={editing?.id ? "Save changes" : "Add indexer"}
        readOnly={locked}
        onSubmit={() => {
          if (!locked && !save.isPending && draft && draft.name.trim() && draft.base_url.trim() && draft.api_key && (!keyRequired || draft.api_key !== MASK)) {
            save.mutate({ ...draft, name: draft.name.trim(), base_url: draft.base_url.trim() });
          }
        }}
        onClose={() => setEditing(null)}
      >
        {draft && (
          <fieldset disabled={locked} className="space-y-4">
            <Field id="indexer-name" label="Name" layout="horizontal">
              <Input id="indexer-name" required value={draft.name} onChange={(e) => setDraft({ ...draft, name: e.target.value })} />
            </Field>
            <Field id="indexer-url" label="Newznab URL" layout="horizontal">
              <Input id="indexer-url" type="url" className="font-mono" required placeholder="https://indexer.example/api" value={draft.base_url} onChange={(e) => setDraft({ ...draft, base_url: e.target.value })} />
            </Field>
            <Field id="indexer-key" label="API key" layout="horizontal">
              <Input
                id="indexer-key"
                type="password"
                className="font-mono"
                {...notAPassword}
                required={draft.api_key !== MASK || keyRequired}
                placeholder={draft.api_key === MASK ? "Stored key, unchanged" : undefined}
                value={draft.api_key === MASK ? "" : draft.api_key}
                onChange={(e) => setDraft({ ...draft, api_key: e.target.value || (editing?.api_key === MASK ? MASK : "") })}
              />
              {keyRequired && draft.api_key === MASK && (
                <p className="text-destructive text-xs">Enter the API key again when changing the indexer origin.</p>
              )}
            </Field>
            <Toggle
              id="indexer-form-enabled"
              label="Enabled"
              checked={draft.enabled}
              onChange={(enabled) => setDraft({ ...draft, enabled })}
            />
          </fieldset>
        )}
      </EditorSheet>
    </SettingsSection>
  );
}
