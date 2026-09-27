import { Button, Input, Select, Toggle } from "~/components/ui";
import { validServer, type PlexServer } from "./plex-api";

export function PlexServerEditor({
  server,
  index,
  busy,
  onChange,
  onRemove,
  onTest,
}: {
  server: PlexServer;
  index: number;
  busy: boolean;
  onChange: (server: PlexServer) => void;
  onRemove: () => void;
  onTest: () => void;
}) {
  const label = `Server ${index + 1}`;
  const update = (patch: Partial<PlexServer>) => onChange({ ...server, ...patch });
  return (
    <fieldset className="space-y-3 rounded-lg border border-base-content/15 p-4" disabled={busy}>
      <legend className="px-2 font-semibold">{server.name || label}</legend>
      <div className="grid gap-3 md:grid-cols-2">
        <label>
          {label} machine ID
          <Input
            aria-label={`${label} machine ID`}
            value={server.id}
            disabled={Boolean(server.handle)}
            onChange={(event) => update({ id: event.target.value })}
          />
        </label>
        <label>
          {label} name
          <Input
            aria-label={`${label} name`}
            value={server.name}
            onChange={(event) => update({ name: event.target.value })}
          />
        </label>
        <label>
          {label} URL
          <Input
            aria-label={`${label} URL`}
            value={server.url}
            placeholder="http://plex-host:32400"
            onChange={(event) => update({ url: event.target.value })}
          />
        </label>
        <label>
          {label} token
          <Input
            aria-label={`${label} token`}
            type="password"
            autoComplete="off"
            value={server.token}
            disabled={Boolean(server.handle)}
            placeholder={
              server.handle ? "Managed by discovery handle" : "Token or existing masked value"
            }
            onChange={(event) => update({ token: event.target.value })}
          />
        </label>
      </div>
      <Toggle
        label={`Enable ${label.toLowerCase()}`}
        checked={server.enabled}
        onChange={(event) => update({ enabled: event.target.checked })}
      />
      {server.accountId && (
        <p className="text-xs">
          Linked account: {server.accountId}. Disconnecting that account disables this server.
        </p>
      )}
      <p className="text-xs text-base-content/60">
        Map an exact Plex file prefix to an imported DAV prefix, or to a local symlink/STRM library
        prefix. No directory scan or title matching is performed.
      </p>
      {server.pathMappings.map((mapping, mappingIndex) => {
        const prefix = `${label} mapping ${mappingIndex + 1}`;
        const edit = (patch: Partial<typeof mapping>) =>
          update({
            pathMappings: server.pathMappings.map((item, position) =>
              position === mappingIndex ? { ...item, ...patch } : item,
            ),
          });
        return (
          <div
            key={mappingIndex}
            className="grid gap-2 rounded border border-base-content/10 p-3 md:grid-cols-3"
          >
            <label>
              {prefix} Plex path
              <Input
                aria-label={`${prefix} Plex path`}
                value={mapping.plexPath}
                onChange={(event) => edit({ plexPath: event.target.value })}
              />
            </label>
            <label>
              {prefix} target type
              <Select
                aria-label={`${prefix} target type`}
                value={
                  mapping.localPath !== undefined && mapping.localPath !== null ? "local" : "dav"
                }
                onChange={(event) =>
                  edit(
                    event.target.value === "local"
                      ? { davPath: null, localPath: "" }
                      : { davPath: "", localPath: null },
                  )
                }
              >
                <option value="dav">Imported DAV path</option>
                <option value="local">Local symlink / STRM path</option>
              </Select>
            </label>
            <label>
              {prefix} target path
              <Input
                aria-label={`${prefix} target path`}
                value={mapping.localPath ?? mapping.davPath ?? ""}
                onChange={(event) =>
                  edit(
                    mapping.localPath !== undefined && mapping.localPath !== null
                      ? { localPath: event.target.value }
                      : { davPath: event.target.value },
                  )
                }
              />
            </label>
            <Button
              type="button"
              onClick={() =>
                update({
                  pathMappings: server.pathMappings.filter(
                    (_, position) => position !== mappingIndex,
                  ),
                })
              }
            >
              Remove mapping {mappingIndex + 1} from server {index + 1}
            </Button>
          </div>
        );
      })}
      <div className="flex flex-wrap gap-2">
        <Button
          type="button"
          disabled={server.pathMappings.length >= 64}
          onClick={() =>
            update({ pathMappings: [...server.pathMappings, { plexPath: "", davPath: "" }] })
          }
        >
          Add path mapping for server {index + 1}
        </Button>
        <Button type="button" disabled={!validServer(server)} onClick={onTest}>
          Test server {index + 1}
        </Button>
        <Button type="button" onClick={onRemove}>
          Remove server {index + 1}
        </Button>
      </div>
    </fieldset>
  );
}
