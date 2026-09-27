import { useEffect, useState } from "react";
import { loadPlexBootstrap, subscribePlexServers, type PlexServer } from "../plex/plex-api";

export function PlexServerStatus() {
  const [servers, setServers] = useState<PlexServer[]>([]);
  const [unavailable, setUnavailable] = useState(false);

  useEffect(() => {
    let alive = true;
    void loadPlexBootstrap()
      .then((result) => {
        if (alive) {
          setServers(result.servers);
          setUnavailable(false);
        }
      })
      .catch(() => {
        if (alive) setUnavailable(true);
      });
    return () => {
      alive = false;
    };
  }, []);
  useEffect(
    () =>
      subscribePlexServers((nextServers) => {
        setServers(nextServers);
        setUnavailable(false);
      }),
    [],
  );

  const enabled = servers.filter((server) => server.enabled);
  const message = unavailable
    ? "Plex connection status unavailable"
    : enabled.length === 0
      ? "No Plex server connected"
      : enabled.length === 1
        ? `${enabled[0]!.name} connected`
        : `${enabled.length} Plex servers connected`;

  return (
    <div className="flex items-center gap-2 text-sm text-base-content/70" role="status">
      <span
        aria-hidden="true"
        className={`size-2.5 rounded-full ${enabled.length > 0 ? "bg-success" : "bg-warning"}`}
      />
      <span>{message}</span>
    </div>
  );
}
