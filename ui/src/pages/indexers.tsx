import { useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Info, Loader2, Pencil, PlugZap, Plus, Trash2 } from "lucide-react";
import { toast } from "sonner";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { ConfirmButton, EditorSheet, Empty, Failure, Field, IconButton, Loading, SettingsSection, Status, Toggle } from "../ui";
import { api, body, HttpError, type Schemas } from "../api/client";
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
  // Counts editor openings and closings, so a save that finishes late cannot close a newer
  // editor; its result is reported with a toast instead.
  const session = useRef(0);
  // Returns the refetch, so a mutation that awaits it stays pending until the list has the new revision.
  const updated = () =>
    Promise.all([
      client.invalidateQueries({ queryKey: ["indexers"] }),
      client.invalidateQueries({ queryKey: ["settings"] }),
    ]);
  const save = useMutation({
    mutationFn: ({ value }: { value: Indexer; base: Indexer; session: number }) =>
      value.id
        ? body(api.PUT("/api/v1/indexers/{id}", { params: { path: { id: value.id } }, body: value }))
        : body(api.POST("/api/v1/indexers", { body: {
            name: value.name,
            base_url: value.base_url,
            api_key: value.api_key,
            enabled: value.enabled,
          } })),
    onSuccess: (saved, { session: from }) => {
      // The cache gets the new revision at once, so an editor opened before the refetch is current.
      client.setQueryData<Indexer[]>(["indexers"], (list) =>
        list && (list.some((i) => i.id === saved.id) ? list.map((i) => (i.id === saved.id ? saved : i)) : [...list, saved]),
      );
      void updated();
      setChecked(null);
      if (from === session.current) setEditing(null);
      else toast.success(`Saved ${saved.name}`);
    },
    onError: async (error, { value, base, session: from }) => {
      await updated();
      if (from !== session.current) {
        toast.error(error.message);
        return;
      }
      // Another session changed this indexer: keep the user's edits on top of the new revision.
      const fresh = error instanceof HttpError && error.status === 409
        ? client.getQueryData<Indexer[]>(["indexers"])?.find((i) => i.id === value.id)
        : undefined;
      if (fresh) {
        const edits = Object.fromEntries(
          (Object.keys(value) as (keyof Indexer)[])
            .filter((key) => key !== "revision" && value[key] !== base[key])
            .map((key) => [key, value[key]]),
        );
        setEditing(fresh);
        setDraft({ ...fresh, ...edits });
      }
    },
  });
  const remove = useMutation({
    mutationFn: (id: string) => body(api.DELETE("/api/v1/indexers/{id}", { params: { path: { id } } })),
    onSuccess: updated,
  });
  const nameMissing = !!draft && draft.name !== "" && !draft.name.trim();
  const urlMissing = !!draft && draft.base_url !== "" && !draft.base_url.trim();
  const toggle = useMutation({
    mutationFn: ({ id, enabled }: { id: string; enabled: boolean }) =>
      body(api.PATCH("/api/v1/indexers/{id}/enabled", { params: { path: { id } }, body: { enabled } })),
    onSuccess: updated,
    onError: updated,
  });
  const test = useMutation({
    mutationFn: ({ id }: Indexer) => body(api.POST("/api/v1/indexers/{id}/test", { params: { path: { id } } })),
    // The server names the revision it tested, which can be newer than this row.
    onSuccess: (result) => setChecked({ revision: result.revision, result }),
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
    session.current += 1;
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
          <Info />
          <AlertDescription>Indexers are set by environment variables and cannot be changed here.</AlertDescription>
        </Alert>
      )}
      {indexers.isPending || settings.isPending ? <Loading /> : indexers.isError ? <Failure error={indexers.error} /> : null}
      {settings.isError && <Failure error={settings.error} />}
      {remove.isError && <Failure error={remove.error} />}
      {toggle.isError && <Failure error={toggle.error} />}
      {test.isError && <Failure error={test.error} />}
      {!indexers.isPending && !indexers.isError && (
        list.length === 0 ? (
          <Empty title="No indexers yet" action={!locked && addButton("outline")}>
            Add a Newznab-compatible indexer to search it and queue its NZBs.
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
                    <div className="mt-1 space-y-0.5" role={checked.result.success ? "status" : "alert"}>
                      <Status tone={checked.result.success ? "success" : "destructive"}>
                        {checked.result.success ? "Connected" : "Can’t connect"}
                      </Status>
                      {!checked.result.success && checked.result.error && (
                        <p className="text-destructive text-xs break-words">{checked.result.error}</p>
                      )}
                    </div>
                  )}
                </div>
                <div className="flex flex-wrap items-center gap-2 max-sm:w-full max-sm:justify-between">
                  <Toggle
                    id={`indexer-enabled-${item.id}`}
                    label={
                      <>
                        Enabled<span className="sr-only"> {item.name}</span>
                      </>
                    }
                    checked={item.enabled}
                    disabled={locked || toggle.isPending}
                    onChange={(enabled) => toggle.mutate({ id: item.id, enabled })}
                  />
                  <Button
                    type="button"
                    variant="outline"
                    size="sm"
                    aria-label={`Test ${item.name}`}
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
                    // An editor opened mid-toggle would keep the old revision and every save would get 409.
                    disabled={toggle.isPending && toggle.variables?.id === item.id}
                    onClick={() => openEditor(item)}
                  >
                    <Pencil />
                  </IconButton>
                  {!locked && (
                    <ConfirmButton
                      icon={<Trash2 />}
                      label={`Remove ${item.name}`}
                      title={`Remove ${item.name}?`}
                      message="Search stops using it right away."
                      confirm="Remove"
                      disabled={remove.isPending}
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
        title={editing?.id ? `${locked ? "View" : "Edit"} ${editing.name}` : "Add indexer"}
        description="Newznab API connection settings."
        dirty={JSON.stringify(editing) !== JSON.stringify(draft)}
        saving={save.isPending}
        error={save.error}
        submit={editing?.id ? "Save changes" : "Add indexer"}
        readOnly={locked}
        onSubmit={() => {
          if (!locked && !save.isPending && editing && draft && draft.name.trim() && draft.base_url.trim() && draft.api_key && (!keyRequired || draft.api_key !== MASK)) {
            save.mutate({
              value: { ...draft, name: draft.name.trim(), base_url: draft.base_url.trim() },
              base: editing,
              session: session.current,
            });
          }
        }}
        onClose={() => {
          session.current += 1;
          setEditing(null);
        }}
      >
        {draft && (
          <fieldset disabled={locked || save.isPending} className="space-y-4">
            <Field id="indexer-name" label="Name" layout="horizontal" error={nameMissing ? "Enter a name." : undefined}>
              <Input id="indexer-name" required value={draft.name} onChange={(e) => setDraft({ ...draft, name: e.target.value })} />
            </Field>
            <Field id="indexer-url" label="Newznab URL" layout="horizontal" error={urlMissing ? "Enter the Newznab URL." : undefined}>
              <Input id="indexer-url" type="url" className="font-mono" required placeholder="https://indexer.example/api" value={draft.base_url} onChange={(e) => setDraft({ ...draft, base_url: e.target.value })} />
            </Field>
            <Field
              id="indexer-key"
              label="API key"
              layout="horizontal"
              error={keyRequired && draft.api_key === MASK ? "Enter the API key again when changing the indexer origin." : undefined}
            >
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
