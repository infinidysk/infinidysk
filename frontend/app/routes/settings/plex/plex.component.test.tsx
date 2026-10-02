// @vitest-environment jsdom
import { act, cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ManagedEnvProvider } from "~/components/ui";
import { PlexSources } from "../smart-prefetch/plex-sources";
import { parsePrefetchSettings } from "../smart-prefetch/smart-prefetch-model";
import { PlexSettings } from "./plex";

function responses(extra: Record<string, unknown> = {}) {
  return vi
    .fn<(url: string, init: RequestInit) => Promise<Response>>()
    .mockImplementation((url, init) => {
      const op = url.split("/api/plex/")[1]!;
      const body = JSON.parse(typeof init.body === "string" ? init.body : "{}") as {
        servers?: unknown;
      };
      const data =
        extra[op] ??
        (
          {
            accounts: { accounts: [] },
            servers: { servers: [] },
            save: { servers: body.servers },
            "login/start": {
              handle: "opaque",
              url: "https://app.plex.tv/auth#pin",
              expiresAt: new Date(Date.now() + 900000).toISOString(),
            },
            "login/poll": { state: "pending" },
            "login/cancel": { status: true },
          } as Record<string, unknown>
        )[op] ??
        {};
      return Promise.resolve(new Response(JSON.stringify(data)));
    });
}
beforeEach(() =>
  vi.stubGlobal(
    "open",
    vi.fn(() => null),
  ),
);
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("Plex settings", () => {
  it("opens the Plex authorization tab from the sign-in button and keeps a fallback link", async () => {
    const replace = vi.fn();
    vi.stubGlobal(
      "open",
      vi.fn(() => ({ opener: null, location: { replace }, close: vi.fn() })),
    );
    vi.stubGlobal("fetch", responses());
    render(<PlexSettings />);
    await userEvent.click(await screen.findByRole("button", { name: "Sign in with Plex" }));
    expect(await screen.findByText("Open Plex sign-in")).toBeTruthy();
    expect(replace).toHaveBeenCalledWith("https://app.plex.tv/auth#pin");
  });

  it("shows a saved server in a compact card with direct test and edit actions", async () => {
    const fetcher = responses({
      servers: {
        servers: [
          {
            id: "machine",
            name: "Home",
            url: "http://home:32400",
            token: "masked",
            enabled: true,
            pathMappings: [],
          },
        ],
      },
    });
    vi.stubGlobal("fetch", fetcher);
    render(<PlexSettings />);
    expect(await screen.findByText("http://home:32400")).toBeTruthy();
    const advanced = screen.getByText("Advanced server configuration").closest("details");
    expect(advanced?.open).toBe(false);
    await userEvent.click(screen.getByRole("button", { name: "Edit server & paths" }));
    expect(advanced?.open).toBe(true);
    await userEvent.click(screen.getByRole("button", { name: "Test server" }));
    await screen.findByText("Home identity and authorization verified.");
    expect(fetcher.mock.calls.some((call) => String(call[0]).endsWith("/api/plex/test"))).toBe(
      true,
    );
  });

  it("makes a saved server available to Smart Prefetch without reloading", async () => {
    vi.stubGlobal("fetch", responses());
    render(
      <>
        <PlexSettings />
        <PlexSources settings={parsePrefetchSettings(undefined)} onChange={vi.fn()} />
      </>,
    );
    await screen.findByRole("button", { name: "Add manual server" });
    expect(screen.queryByRole("option", { name: "Home" })).toBeNull();
    await userEvent.click(screen.getByRole("button", { name: "Add manual server" }));
    await userEvent.type(screen.getByLabelText("Server 1 machine ID"), "machine");
    await userEvent.type(screen.getByLabelText("Server 1 name"), "Home");
    await userEvent.type(screen.getByLabelText("Server 1 URL"), "http://localhost:32400");
    await userEvent.type(screen.getByLabelText("Server 1 token"), "secret");
    await userEvent.click(screen.getByRole("button", { name: "Add path mapping for server 1" }));
    await userEvent.type(screen.getByLabelText("Server 1 mapping 1 Plex path"), "/Plex");
    await userEvent.selectOptions(screen.getByLabelText("Server 1 mapping 1 target type"), "local");
    await userEvent.type(screen.getByLabelText("Server 1 mapping 1 target path"), "/mnt/library");
    await userEvent.click(screen.getByRole("button", { name: "Save advanced server changes" }));
    expect(await screen.findByRole("option", { name: "Home" })).toBeTruthy();
  });
  it("removes a disconnected server from Smart Prefetch without reloading", async () => {
    let disconnected = false;
    const base = responses({
      servers: {
        servers: [
          {
            id: "machine",
            name: "Home",
            url: "http://localhost:32400",
            token: "masked-token",
            enabled: true,
            pathMappings: [],
          },
        ],
      },
    });
    vi.stubGlobal(
      "fetch",
      vi.fn().mockImplementation((url: string, init: RequestInit) => {
        const operation = url.split("/api/plex/")[1];
        if (operation === "disconnect") {
          disconnected = true;
          return Promise.resolve(new Response(JSON.stringify({ status: true })));
        }
        if (operation === "servers" && disconnected)
          return Promise.resolve(new Response(JSON.stringify({ servers: [] })));
        return base(url, init);
      }),
    );
    vi.stubGlobal(
      "confirm",
      vi.fn(() => true),
    );
    render(
      <>
        <PlexSettings />
        <PlexSources settings={parsePrefetchSettings(undefined)} onChange={vi.fn()} />
      </>,
    );

    expect(await screen.findByRole("option", { name: "Home" })).toBeTruthy();
    await userEvent.click(screen.getByText("Advanced server configuration"));
    await userEvent.click(screen.getByRole("button", { name: "Add manual server" }));
    await userEvent.type(screen.getByLabelText("Server 2 name"), "Unsaved draft");
    await userEvent.click(screen.getByRole("button", { name: "Remove server 1" }));

    await waitFor(() => expect(screen.queryByRole("option", { name: "Home" })).toBeNull());
    expect(screen.queryByRole("option", { name: "Unsaved draft" })).toBeNull();
  });
  it("allows readonly selection of an environment-managed account for server discovery", async () => {
    vi.stubGlobal(
      "fetch",
      responses({
        accounts: { accounts: [{ id: "account", name: "Owner", token: "masked" }] },
        "account/select": {
          handle: "account-handle",
          url: "",
          expiresAt: new Date(Date.now() + 900000).toISOString(),
        },
      }),
    );
    render(
      <ManagedEnvProvider value={{ "plex.accounts": "NZBDAV_CONFIG__PLEX__ACCOUNTS" }}>
        <PlexSettings />
      </ManagedEnvProvider>,
    );
    const select = await screen.findByRole("button", { name: "Use account Owner" });
    expect(select.closest("fieldset")?.disabled ?? false).toBe(false);
    await userEvent.click(select);
    expect(await screen.findByRole("button", { name: "Discover Plex servers" })).toBeTruthy();
  });
  it("saves the selected discovered server without dropping existing servers", async () => {
    const fetcher = responses({
      accounts: { accounts: [{ id: "account", name: "Owner", token: "masked" }] },
      servers: {
        servers: [
          {
            id: "existing-machine",
            name: "Existing",
            url: "http://existing:32400",
            token: "masked-existing-token",
            enabled: true,
            pathMappings: [],
          },
        ],
      },
      "account/select": {
        handle: "account-handle",
        url: "",
        expiresAt: new Date(Date.now() + 900000).toISOString(),
      },
      discover: {
        servers: [
          {
            handle: "home-handle",
            id: "home-machine",
            name: "Home",
            connections: [{ uri: "http://home:32400", local: true, relay: false }],
          },
          {
            handle: "backup-handle",
            id: "backup-machine",
            name: "Backup",
            connections: [{ uri: "https://backup:32400", local: false, relay: false }],
          },
        ],
      },
    });
    vi.stubGlobal("fetch", fetcher);
    render(<PlexSettings />);

    await userEvent.click(await screen.findByRole("button", { name: "Use account Owner" }));
    await userEvent.click(await screen.findByRole("button", { name: "Discover Plex servers" }));

    const save = await screen.findByRole("button", { name: "Save selected server" });
    expect(save.hasAttribute("disabled")).toBe(true);
    expect(fetcher.mock.calls.some((call) => String(call[0]).endsWith("/api/plex/save"))).toBe(
      false,
    );

    await userEvent.click(screen.getByRole("radio", { name: "Select Plex server Home" }));
    expect(save.hasAttribute("disabled")).toBe(false);
    await userEvent.click(save);

    await screen.findByText("Home saved. It is ready for Smart Prefetch.");
    const saveCall = fetcher.mock.calls.find((call) => String(call[0]).endsWith("/api/plex/save"));
    const body = JSON.parse(typeof saveCall?.[1].body === "string" ? saveCall[1].body : "{}") as {
      servers: { id: string; handle?: string }[];
    };
    expect(body.servers).toEqual(
      expect.arrayContaining([
        expect.objectContaining({ id: "existing-machine" }),
        expect.objectContaining({ id: "home-machine", handle: "home-handle" }),
      ]),
    );
    expect(body.servers).toHaveLength(2);
  });
  it("does not revive a cancelled sign-in when an earlier poll completes", async () => {
    let release!: (response: Response) => void;
    const base = responses();
    vi.stubGlobal(
      "fetch",
      vi.fn().mockImplementation((url: string, init: RequestInit) =>
        url.endsWith("login/poll")
          ? new Promise<Response>((resolve) => {
              release = resolve;
            })
          : base(url, init),
      ),
    );
    render(<PlexSettings />);
    await userEvent.click(await screen.findByRole("button", { name: "Sign in with Plex" }));
    await userEvent.click(await screen.findByRole("button", { name: "Check sign-in" }));
    await userEvent.click(screen.getByRole("button", { name: "Cancel sign-in" }));
    await screen.findByText("Sign-in cancelled.");
    await act(async () => {
      release(new Response(JSON.stringify({ state: "connected" })));
      await Promise.resolve();
    });
    expect(screen.getByText("Sign-in cancelled.")).toBeTruthy();
    expect(screen.queryByText("Account ready for server discovery.")).toBeNull();
  });
  it("selects a protected Home user then discovers, tests and saves a server handle", async () => {
    const fetcher = responses({
      accounts: { accounts: [{ id: "account", name: "Owner", token: "masked" }] },
      "account/select": {
        handle: "account-handle",
        url: "",
        expiresAt: new Date(Date.now() + 900000).toISOString(),
      },
      "home/users": { users: [{ id: "kid", name: "Child", protected: true, admin: false }] },
      "home/switch": {
        handle: "child-handle",
        accountId: "kid",
        expiresAt: new Date(Date.now() + 900000).toISOString(),
      },
      discover: {
        servers: [
          {
            handle: "server-handle",
            id: "machine",
            name: "Home",
            connections: [{ uri: "http://localhost:32400", local: true, relay: false }],
          },
        ],
      },
      test: { success: true, machineIdentifier: "machine" },
    });
    vi.stubGlobal("fetch", fetcher);
    render(<PlexSettings />);
    await userEvent.click(await screen.findByRole("button", { name: "Use account Owner" }));
    await userEvent.click(await screen.findByRole("button", { name: "Load Plex Home users" }));
    await userEvent.selectOptions(await screen.findByLabelText("Plex Home user"), "kid");
    await userEvent.type(screen.getByLabelText("Plex Home PIN"), "1234");
    await userEvent.click(screen.getByRole("button", { name: "Switch Plex Home user" }));
    await screen.findByText("Plex Home user connected and saved.");
    await userEvent.click(screen.getByRole("button", { name: "Discover Plex servers" }));
    await userEvent.click(await screen.findByRole("radio", { name: "Select Plex server Home" }));
    await userEvent.click(screen.getByRole("button", { name: "Save selected server" }));
    await waitFor(() =>
      expect(fetcher).toHaveBeenCalledWith(
        expect.stringContaining("/api/plex/save"),
        expect.objectContaining({
          body: expect.stringContaining('"handle":"server-handle"') as unknown,
        }),
      ),
    );
    const switched = fetcher.mock.calls.find((call) => String(call[0]).endsWith("home/switch"));
    expect(JSON.parse(typeof switched?.[1].body === "string" ? switched[1].body : "{}")).toEqual({
      handle: "account-handle",
      userId: "kid",
      pin: "1234",
    });
    expect(fetcher.mock.calls.every((call) => !String(call[0]).includes("1234"))).toBe(true);
  });
  it("shows expired login and permits a fresh retry", async () => {
    vi.stubGlobal(
      "fetch",
      responses({
        "login/start": {
          handle: "expired",
          url: "https://app.plex.tv/auth",
          expiresAt: "2000-01-01T00:00:00Z",
        },
      }),
    );
    render(<PlexSettings />);
    await userEvent.click(await screen.findByRole("button", { name: "Sign in with Plex" }));
    await userEvent.click(await screen.findByRole("button", { name: "Check sign-in" }));
    expect(await screen.findByText(/Sign-in expired/)).toBeTruthy();
    expect(screen.getByRole("button", { name: "Sign in with Plex" })).toBeTruthy();
  });
  it("edits and saves manual servers with exact local mapping through the dedicated API", async () => {
    const fetcher = responses();
    vi.stubGlobal("fetch", fetcher);
    render(<PlexSettings />);
    await screen.findByRole("button", { name: "Add manual server" });
    await userEvent.click(screen.getByRole("button", { name: "Add manual server" }));
    await userEvent.type(screen.getByLabelText("Server 1 machine ID"), "machine");
    await userEvent.type(screen.getByLabelText("Server 1 name"), "Home");
    await userEvent.type(screen.getByLabelText("Server 1 URL"), "http://localhost:32400");
    await userEvent.type(screen.getByLabelText("Server 1 token"), "secret");
    await userEvent.click(screen.getByRole("button", { name: "Add path mapping for server 1" }));
    await userEvent.type(screen.getByLabelText("Server 1 mapping 1 Plex path"), "/Plex");
    await userEvent.selectOptions(screen.getByLabelText("Server 1 mapping 1 target type"), "local");
    await userEvent.type(screen.getByLabelText("Server 1 mapping 1 target path"), "/mnt/library");
    await userEvent.click(screen.getByRole("button", { name: "Save advanced server changes" }));
    await waitFor(() =>
      expect(fetcher).toHaveBeenCalledWith(
        expect.stringContaining("/api/plex/save"),
        expect.objectContaining({
          body: expect.stringContaining('"localPath":"/mnt/library"') as unknown,
        }),
      ),
    );
    expect(fetcher.mock.calls.some((call) => String(call[0]).includes("update-config"))).toBe(
      false,
    );
  });
  it("shows browser PIN pending, cancellation and retry without exposing tokens", async () => {
    const fetcher = responses();
    vi.stubGlobal("fetch", fetcher);
    render(<PlexSettings />);
    await userEvent.click(await screen.findByRole("button", { name: "Sign in with Plex" }));
    expect(await screen.findByRole("link", { name: "Open Plex sign-in" })).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Check sign-in" }));
    expect(await screen.findByText(/Waiting for Plex authorization/)).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Cancel sign-in" }));
    expect(await screen.findByText("Sign-in cancelled.")).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Sign in with Plex" }));
    expect(
      fetcher.mock.calls.filter((call) => String(call[0]).endsWith("login/start")),
    ).toHaveLength(2);
  });
  it("pins environment-managed credentials", async () => {
    vi.stubGlobal("fetch", responses());
    render(
      <ManagedEnvProvider
        value={{
          "plex.accounts": "NZBDAV_CONFIG__PLEX__ACCOUNTS",
          "plex.servers": "NZBDAV_CONFIG__PLEX__SERVERS",
        }}
      >
        <PlexSettings />
      </ManagedEnvProvider>,
    );
    await screen.findByRole("button", { name: "Add manual server" });
    expect(
      screen.getByRole("button", { name: "Add manual server" }).closest("fieldset")?.disabled,
    ).toBe(true);
    expect(
      screen.getByRole("button", { name: "Sign in with Plex" }).closest("fieldset")?.disabled,
    ).toBe(true);
  });
});
