import { useCallback, useEffect, useRef, useState } from "react";
import { Alert, Button, Input, ManagedSetting, Select, SettingsCard } from "~/components/ui";
import {
  loadPlexBootstrap,
  plexRequest,
  publishPlexServers,
  serverSaveRequest,
  validServer,
  type PlexAccount,
  type PlexCandidate,
  type PlexHomeUser,
  type PlexLogin,
  type PlexServer,
} from "./plex-api";
import { PlexServerEditor } from "./plex-server-editor";

export function PlexSettings() {
  const [accounts, setAccounts] = useState<PlexAccount[]>([]);
  const [servers, setServers] = useState<PlexServer[]>([]);
  const [savedIds, setSavedIds] = useState<string[]>([]);
  const [loaded, setLoaded] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState("");
  const [login, setLogin] = useState<PlexLogin | null>(null);
  const [state, setState] = useState("idle");
  const [homeUsers, setHomeUsers] = useState<PlexHomeUser[]>([]);
  const [homeId, setHomeId] = useState("");
  const [pin, setPin] = useState("");
  const [candidates, setCandidates] = useState<PlexCandidate[]>([]);
  const [selectedCandidateHandle, setSelectedCandidateHandle] = useState("");
  const [connections, setConnections] = useState<Record<string, string>>({});
  const polling = useRef(false);
  const advancedServers = useRef<HTMLDetailsElement>(null);
  const loginGeneration = useRef(0);
  const [verifiedServerId, setVerifiedServerId] = useState<string | null>(null);
  const refreshAccounts = async () =>
    setAccounts((await plexRequest<{ accounts: PlexAccount[] }>("accounts")).accounts);
  const refreshServers = async () => {
    const result = (await plexRequest<{ servers: PlexServer[] }>("servers")).servers;
    setServers(result);
    setSavedIds(result.map((server) => server.id));
    setVerifiedServerId(null);
    publishPlexServers(result);
  };
  useEffect(() => {
    let alive = true;
    const generation = loginGeneration;
    void loadPlexBootstrap()
      .then((result) => {
        if (alive) {
          setAccounts(result.accounts);
          setServers(result.servers);
          setSavedIds(result.servers.map((server) => server.id));
          setLoaded(true);
        }
      })
      .catch((cause) => {
        if (alive)
          setError(cause instanceof Error ? cause.message : "Plex settings could not be loaded.");
      });
    return () => {
      alive = false;
      generation.current++;
    };
  }, []);
  const act = async (operation: () => Promise<void>) => {
    setBusy(true);
    setError(null);
    setMessage("");
    try {
      await operation();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Plex operation failed.");
    } finally {
      setBusy(false);
    }
  };
  const checkLogin = useCallback(async () => {
    if (!login || state !== "pending" || polling.current) return;
    if (Date.now() >= Date.parse(login.expiresAt)) {
      setState("expired");
      return;
    }
    polling.current = true;
    const generation = loginGeneration.current;
    try {
      const result = await plexRequest<{ state: string }>("login/poll", { handle: login.handle });
      if (generation !== loginGeneration.current) return;
      setState(result.state);
      if (result.state === "connected") {
        const saved = await plexRequest<{ accounts: PlexAccount[] }>("accounts");
        if (generation !== loginGeneration.current) return;
        setAccounts(saved.accounts);
        setMessage("Plex account connected and saved.");
      }
    } catch (cause) {
      if (generation === loginGeneration.current) {
        setError(cause instanceof Error ? cause.message : "Sign-in could not be checked.");
        setState("failed");
      }
    } finally {
      polling.current = false;
    }
  }, [login, state]);
  useEffect(() => {
    if (state !== "pending") return;
    const timer = setInterval(() => void checkLogin(), 2000);
    return () => clearInterval(timer);
  }, [state, checkLogin]);
  const start = () => {
    // Open the tab during the click gesture so browsers do not block the Plex sign-in.
    // The link shown in the pending state remains available when pop-ups are blocked.
    let authWindow: Window | null = null;
    try {
      authWindow = globalThis.open?.("about:blank", "_blank") ?? null;
      if (authWindow) authWindow.opener = null;
    } catch {
      // A sign-in link is shown below if the browser cannot open a tab.
    }
    return act(async () => {
      try {
        loginGeneration.current++;
        if (login) await plexRequest("login/cancel", { handle: login.handle });
        setHomeUsers([]);
        setCandidates([]);
        setPin("");
        const next = await plexRequest<PlexLogin>("login/start");
        setLogin(next);
        setState("pending");
        try {
          authWindow?.location.replace(next.url);
        } catch {
          authWindow?.close();
          // The visible sign-in link still lets the user finish authorization.
        }
      } catch (cause) {
        authWindow?.close();
        throw cause;
      }
    });
  };
  const select = (accountId: string) =>
    act(async () => {
      loginGeneration.current++;
      const selected = await plexRequest<PlexLogin>("account/select", { accountId });
      setLogin(selected);
      setState("connected");
      setHomeUsers([]);
      setCandidates([]);
      setPin("");
    });
  const saveServers = async (nextServers: PlexServer[], successMessage: string) => {
    const result = await plexRequest<{ servers: PlexServer[] }>("save", {
      servers: nextServers.map(serverSaveRequest),
    });
    setServers(result.servers);
    setSavedIds(result.servers.map((server) => server.id));
    setVerifiedServerId(null);
    publishPlexServers(result.servers);
    setMessage(successMessage);
  };
  const save = () =>
    act(async () => {
      await saveServers(servers, "Plex servers saved. General Apply is not required.");
    });
  const discover = () =>
    act(async () => {
      const result = await plexRequest<{ servers: PlexCandidate[] }>("discover", {
        handle: login?.handle,
      });
      setCandidates(result.servers);
      setSelectedCandidateHandle((current) =>
        result.servers.some((candidate) => candidate.handle === current) ? current : "",
      );
    });
  const selectedCandidate = candidates.find(
    (candidate) => candidate.handle === selectedCandidateHandle,
  );
  const saveSelectedCandidate = () =>
    act(async () => {
      if (!selectedCandidate) return;
      const existing = servers.find((server) => server.id === selectedCandidate.id);
      const selected: PlexServer = {
        id: selectedCandidate.id,
        name: selectedCandidate.name,
        url: connections[selectedCandidate.handle] ?? selectedCandidate.connections[0]?.uri ?? "",
        token: "",
        enabled: true,
        pathMappings: existing?.pathMappings ?? [],
        handle: selectedCandidate.handle,
      };
      const nextServers = existing
        ? servers.map((server) => (server.id === selected.id ? selected : server))
        : [...servers, selected];
      await saveServers(
        nextServers,
        `${selectedCandidate.name} saved. It is ready for Smart Prefetch.`,
      );
    });
  return (
    <div className="space-y-5">
      {error && <Alert variant="danger">{error}</Alert>}
      {message && <p role="status">{message}</p>}
      <SettingsCard
        icon="account_circle"
        title="Plex connection"
        description="Connect an account, choose a server, then test it. Credentials stay on this server."
      >
        {!loaded ? (
          <p>Loading Plex settings…</p>
        ) : (
          <>
            {accounts.length === 0 && (
              <div className="rounded-lg border border-base-content/10 bg-base-200/30 p-4">
                <p className="font-semibold">1. Sign in to Plex</p>
                <p className="mt-1 text-sm text-base-content/60">
                  Authorize InfiniDysk in a new tab, then return here to discover your servers.
                </p>
              </div>
            )}
            <div className="space-y-2">
              {accounts.map((account) => (
                <div
                  key={account.id}
                  className="flex flex-wrap items-center justify-between gap-3 rounded-lg border border-base-content/10 bg-base-200/30 px-4 py-3"
                >
                  <div>
                    <p className="font-semibold">
                      Connected to Plex{" "}
                      <span className="badge badge-success badge-soft badge-sm ml-2">
                        Connected
                      </span>
                    </p>
                    <p className="text-sm text-base-content/60">{account.name} · Saved account</p>
                  </div>
                  <Button type="button" disabled={busy} onClick={() => void select(account.id)}>
                    Use account {account.name}
                  </Button>
                </div>
              ))}
            </div>
            <ManagedSetting configKey="plex.accounts">
              <Button
                type="button"
                variant={accounts.length ? "outline" : "primary"}
                disabled={busy}
                onClick={() => void start()}
              >
                Sign in with Plex
              </Button>
              <p className="mt-2 text-xs text-base-content/60">
                Account changes save immediately, separately from General Apply.
              </p>
              {accounts.map((account) => (
                <details
                  key={account.id}
                  className="mt-3 rounded-lg border border-base-content/10 bg-base-200/30"
                >
                  <summary className="cursor-pointer px-3 py-2 text-sm font-semibold">
                    Manage {account.name} account
                  </summary>
                  <div className="flex flex-wrap gap-2 border-t border-base-content/10 p-3">
                    <Button type="button" disabled={busy} onClick={() => void start()}>
                      Reconnect account {account.name}
                    </Button>
                    <Button
                      type="button"
                      variant="danger"
                      disabled={busy}
                      onClick={() => {
                        if (
                          !globalThis.confirm(
                            "Remove this saved account and disable its linked servers? Independent manually configured servers and cached files are retained. This does not revoke authorization at Plex. Unsaved server edits will reload.",
                          )
                        )
                          return;
                        void act(async () => {
                          loginGeneration.current++;
                          await plexRequest("account/disconnect", { accountId: account.id });
                          setLogin(null);
                          setState("idle");
                          setCandidates([]);
                          setHomeUsers([]);
                          await refreshAccounts();
                          await refreshServers();
                        });
                      }}
                    >
                      Disconnect account {account.name}
                    </Button>
                  </div>
                </details>
              ))}
              {state === "pending" && login && (
                <div className="my-3 space-y-2 rounded-lg border border-primary/30 bg-primary/5 p-4">
                  <p>
                    Waiting for Plex authorization. Expires{" "}
                    {new Date(login.expiresAt).toLocaleTimeString()}.
                  </p>
                  <a className="link" href={login.url} target="_blank" rel="noreferrer noopener">
                    Open Plex sign-in
                  </a>
                  <div className="flex gap-2">
                    <Button type="button" disabled={busy} onClick={() => void checkLogin()}>
                      Check sign-in
                    </Button>
                    <Button
                      type="button"
                      disabled={busy}
                      onClick={() =>
                        void act(async () => {
                          loginGeneration.current++;
                          await plexRequest("login/cancel", { handle: login.handle });
                          setState("cancelled");
                          setLogin(null);
                        })
                      }
                    >
                      Cancel sign-in
                    </Button>
                  </div>
                </div>
              )}
              {state === "expired" && (
                <p role="status">Sign-in expired. Sign in with Plex again to retry.</p>
              )}
              {state === "cancelled" && <p role="status">Sign-in cancelled.</p>}
              {state === "failed" && (
                <p role="status">Sign-in failed. Sign in with Plex again to retry.</p>
              )}
              {state === "connected" && login && (
                <details className="mt-3 rounded-lg border border-base-content/10 bg-base-200/30">
                  <summary className="cursor-pointer px-3 py-2 text-sm font-semibold">
                    Plex Home users
                  </summary>
                  <div className="space-y-3 border-t border-base-content/10 p-3">
                    <Button
                      type="button"
                      disabled={busy}
                      onClick={() =>
                        void act(async () => {
                          setHomeUsers(
                            (
                              await plexRequest<{ users: PlexHomeUser[] }>("home/users", {
                                handle: login.handle,
                              })
                            ).users,
                          );
                          setHomeId("");
                        })
                      }
                    >
                      Load Plex Home users
                    </Button>
                    {homeUsers.length > 0 && (
                      <>
                        <label>
                          Plex Home user
                          <Select
                            aria-label="Plex Home user"
                            value={homeId}
                            onChange={(event) => setHomeId(event.target.value)}
                          >
                            <option value="">Choose user</option>
                            {homeUsers.map((user) => (
                              <option key={user.id} value={user.id}>
                                {user.name}
                                {user.protected ? " (PIN protected)" : ""}
                              </option>
                            ))}
                          </Select>
                        </label>
                        {homeUsers.find((user) => user.id === homeId)?.protected && (
                          <label>
                            Plex Home PIN
                            <Input
                              aria-label="Plex Home PIN"
                              type="password"
                              inputMode="numeric"
                              autoComplete="off"
                              maxLength={4}
                              value={pin}
                              onChange={(event) => setPin(event.target.value)}
                            />
                          </label>
                        )}
                        <Button
                          type="button"
                          disabled={
                            busy ||
                            !homeId ||
                            Boolean(
                              homeUsers.find((user) => user.id === homeId)?.protected &&
                              !/^\d{4}$/.test(pin),
                            )
                          }
                          onClick={() =>
                            void act(async () => {
                              try {
                                const switched = await plexRequest<{
                                  handle: string;
                                  expiresAt: string;
                                }>("home/switch", { handle: login.handle, userId: homeId, pin });
                                setLogin({ ...switched, url: "" });
                                setCandidates([]);
                                setHomeUsers([]);
                                await refreshAccounts();
                                setMessage("Plex Home user connected and saved.");
                              } finally {
                                setPin("");
                              }
                            })
                          }
                        >
                          Switch Plex Home user
                        </Button>
                      </>
                    )}
                  </div>
                </details>
              )}
            </ManagedSetting>
            <p className="text-xs text-base-content/60">
              Disconnect removes local account credentials and disables linked servers. Independent
              manually token-configured servers and cached media remain. Revoke remote authorization
              separately in Plex.
            </p>
          </>
        )}
      </SettingsCard>
      {loaded && (
        <SettingsCard
          icon="dns"
          title="Plex servers"
          description="Choose a connection address and test it. Server changes save here without General Apply."
        >
          <ManagedSetting configKey="plex.servers">
            {state === "connected" && login && (
              <Button
                type="button"
                className="mb-5"
                aria-label="Discover Plex servers"
                disabled={busy}
                onClick={() => void discover()}
              >
                {candidates.length ? "Refresh discovery" : "Discover Plex servers"}
              </Button>
            )}
            {servers.length === 0 && candidates.length === 0 && (
              <div className="rounded-lg border border-base-content/10 bg-base-200/30 p-4">
                <p className="font-semibold">2. Choose a server</p>
                <p className="mt-1 text-sm text-base-content/60">
                  {accounts.length
                    ? "Use a saved account, then discover its servers. Open Advanced server configuration to add one manually."
                    : "Your servers will appear here after sign-in."}
                </p>
              </div>
            )}
            {servers.length > 0 && (
              <div className="space-y-3">
                {servers
                  .filter((server) => savedIds.includes(server.id))
                  .map((server) => (
                    <div
                      key={server.id}
                      className="rounded-lg border border-base-content/10 bg-base-200/30 p-4"
                    >
                      <div className="flex flex-wrap items-start justify-between gap-4">
                        <div className="min-w-0 space-y-1">
                          <div className="flex flex-wrap items-center gap-2">
                            <h3 className="font-semibold">{server.name || server.id}</h3>
                            <span
                              className={`badge badge-sm ${server.enabled ? "badge-success badge-soft" : "badge-ghost"}`}
                            >
                              {server.enabled ? "Enabled" : "Disabled"}
                            </span>
                            {verifiedServerId === server.id && (
                              <span className="badge badge-success badge-soft badge-sm">
                                Tested
                              </span>
                            )}
                          </div>
                          <p className="text-xs text-base-content/60">Connection address</p>
                          <p className="break-all text-sm font-medium">{server.url}</p>
                        </div>
                        <div className="flex flex-wrap gap-2">
                          <Button
                            type="button"
                            disabled={busy}
                            onClick={() =>
                              void act(async () => {
                                await plexRequest("test", { server: serverSaveRequest(server) });
                                setVerifiedServerId(server.id);
                                setMessage(
                                  `${server.name || server.id} identity and authorization verified.`,
                                );
                              })
                            }
                          >
                            Test server
                          </Button>
                          <Button
                            type="button"
                            onClick={() => {
                              if (advancedServers.current) advancedServers.current.open = true;
                            }}
                          >
                            Edit server &amp; paths
                          </Button>
                        </div>
                      </div>
                    </div>
                  ))}
              </div>
            )}
            {candidates.length > 0 && (
              <div className="mt-5 space-y-3">
                <div className="flex items-center justify-between gap-3">
                  <h3 className="text-base font-semibold">Detected servers</h3>
                  <span className="badge badge-success badge-soft">{candidates.length} found</span>
                </div>
                {candidates.map((candidate) => {
                  const selected = selectedCandidateHandle === candidate.handle;
                  return (
                    <div
                      key={candidate.handle}
                      className={`space-y-3 rounded-lg border p-4 transition-colors ${
                        selected
                          ? "border-primary bg-primary/10"
                          : "border-base-content/10 bg-base-200/30"
                      }`}
                    >
                      <label className="flex cursor-pointer items-start gap-3">
                        <input
                          className="radio radio-primary mt-0.5"
                          type="radio"
                          name="plex-discovered-server"
                          aria-label={`Select Plex server ${candidate.name}`}
                          checked={selected}
                          onChange={() => setSelectedCandidateHandle(candidate.handle)}
                        />
                        <span className="min-w-0">
                          <strong className="block">{candidate.name}</strong>
                          <span className="text-xs text-base-content/50">{candidate.id}</span>
                        </span>
                      </label>
                      <label>
                        Connection for {candidate.name}
                        <Select
                          aria-label={`Connection for ${candidate.name}`}
                          value={
                            connections[candidate.handle] ?? candidate.connections[0]?.uri ?? ""
                          }
                          onChange={(event) =>
                            setConnections((current) => ({
                              ...current,
                              [candidate.handle]: event.target.value,
                            }))
                          }
                        >
                          {candidate.connections.map((connection) => (
                            <option key={connection.uri} value={connection.uri}>
                              {connection.uri} (
                              {connection.relay ? "relay" : connection.local ? "local" : "remote"})
                            </option>
                          ))}
                        </Select>
                      </label>
                    </div>
                  );
                })}
                <div className="flex flex-wrap gap-2">
                  <Button
                    type="button"
                    variant="primary"
                    disabled={
                      busy || !selectedCandidate || selectedCandidate.connections.length === 0
                    }
                    onClick={() => void saveSelectedCandidate()}
                  >
                    Save selected server
                  </Button>
                  <Button type="button" disabled={busy} onClick={() => void discover()}>
                    Refresh
                  </Button>
                </div>
              </div>
            )}
            <details
              ref={advancedServers}
              className="collapse collapse-arrow mt-5 border border-base-content/10 bg-base-200/40"
            >
              <summary className="collapse-title text-sm font-semibold">
                Advanced server configuration
              </summary>
              <div className="collapse-content space-y-3">
                {servers.map((server, index) => (
                  <PlexServerEditor
                    key={index}
                    server={server}
                    index={index}
                    busy={busy}
                    onChange={(next) =>
                      setServers((current) =>
                        current.map((item, position) => (position === index ? next : item)),
                      )
                    }
                    onTest={() =>
                      void act(async () => {
                        await plexRequest("test", { server: serverSaveRequest(server) });
                        setMessage(`Server ${index + 1} identity and authorization verified.`);
                      })
                    }
                    onRemove={() =>
                      void act(async () => {
                        if (savedIds.includes(server.id)) {
                          if (
                            !globalThis.confirm(
                              "Remove this saved server? Cached media is retained.",
                            )
                          )
                            return;
                          await plexRequest("disconnect", { serverId: server.id });
                          await refreshServers();
                          return;
                        }
                        const nextServers = servers.filter((_, position) => position !== index);
                        setServers(nextServers);
                      })
                    }
                  />
                ))}
                <div className="flex flex-wrap gap-2">
                  <Button
                    type="button"
                    disabled={busy || servers.length >= 32}
                    onClick={() =>
                      setServers((current) => [
                        ...current,
                        {
                          id: "",
                          name: "",
                          url: "",
                          token: "",
                          enabled: true,
                          pathMappings: [],
                        },
                      ])
                    }
                  >
                    Add manual server
                  </Button>
                  <Button
                    type="button"
                    disabled={
                      busy || servers.length === 0 || servers.some((server) => !validServer(server))
                    }
                    onClick={() => void save()}
                  >
                    Save advanced server changes
                  </Button>
                </div>
              </div>
            </details>
          </ManagedSetting>
        </SettingsCard>
      )}
    </div>
  );
}
