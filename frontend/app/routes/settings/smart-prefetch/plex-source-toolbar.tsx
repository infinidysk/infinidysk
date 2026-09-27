import { useState } from "react";
import { Button, Select, Toggle } from "~/components/ui";
import type { PlexServer, PlexUser } from "../plex/plex-api";

type PlexSourceToolbarProps = {
  servers: PlexServer[];
  serverId: string;
  users: PlexUser[];
  selectedUsers: string[];
  busy: boolean;
  lastSuccess: string | null;
  stale: boolean;
  error?: string | null;
  onServerChange: (serverId: string) => void;
  onUsersChange: (users: string[]) => void;
  onRefresh: () => void;
};

export function PlexSourceToolbar({
  servers,
  serverId,
  users,
  selectedUsers,
  busy,
  lastSuccess,
  stale,
  error,
  onServerChange,
  onUsersChange,
  onRefresh,
}: PlexSourceToolbarProps) {
  const [choosingProfiles, setChoosingProfiles] = useState(false);
  const prefix = `${serverId}:`;
  const selectedOnServer = selectedUsers.filter((user) => user.startsWith(prefix));
  const otherServers = selectedUsers.filter((user) => !user.startsWith(prefix));
  const profileValue =
    selectedOnServer.length === 0
      ? ""
      : selectedOnServer.length === 1
        ? selectedOnServer[0]
        : "__multiple__";
  return (
    <div className="space-y-3 rounded-2xl border border-base-content/15 bg-base-200/40 p-4">
      <div className="grid gap-3 md:grid-cols-[minmax(0,1fr)_minmax(0,1fr)_auto] md:items-end">
        <label className="grid gap-1 text-sm">
          Plex server
          <Select
            aria-label="Plex source server"
            value={serverId}
            onChange={(event) => onServerChange(event.target.value)}
          >
            <option value="">Choose server</option>
            {servers.map((server) => (
              <option key={server.id} value={server.id}>
                {server.name}
                {server.enabled ? "" : " (disabled)"}
              </option>
            ))}
          </Select>
        </label>
        <label className="grid gap-1 text-sm">
          Watching profile
          <Select
            aria-label="Watching profile"
            value={profileValue}
            disabled={!serverId}
            onChange={(event) => {
              if (event.target.value === "__multiple__") {
                setChoosingProfiles(true);
                return;
              }
              setChoosingProfiles(false);
              onUsersChange(
                event.target.value ? [...otherServers, event.target.value] : otherServers,
              );
            }}
          >
            <option value="">All Plex users</option>
            {users.map((user) => (
              <option key={user.id} value={`${serverId}:${user.id}`}>
                {user.name}
              </option>
            ))}
            <option value="__multiple__">Choose profiles...</option>
          </Select>
        </label>
        <Button className="min-h-11" type="button" disabled={busy || !serverId} onClick={onRefresh}>
          Refresh Plex catalogue
        </Button>
      </div>
      {(choosingProfiles || selectedOnServer.length > 1) && (
        <div className="flex flex-wrap gap-2" aria-label="Selected Plex profiles">
          {users.map((user) => {
            const id = `${serverId}:${user.id}`;
            return (
              <Toggle
                className="min-h-11"
                key={id}
                label={user.name}
                checked={selectedUsers.includes(id)}
                onChange={(event) =>
                  onUsersChange(
                    event.target.checked
                      ? [...selectedUsers, id]
                      : selectedUsers.filter((item) => item !== id),
                  )
                }
              />
            );
          })}
        </div>
      )}
      <p className="text-xs text-base-content/60">
        {stale ? "Catalogue is stale" : "Catalogue updated"}{" "}
        {lastSuccess ? new Date(lastSuccess).toLocaleString() : "not yet"}
        {error ? ` · ${error}` : ""}
      </p>
    </div>
  );
}
