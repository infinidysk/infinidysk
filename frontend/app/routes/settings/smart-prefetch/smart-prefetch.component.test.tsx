// @vitest-environment jsdom
import { act, cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ManagedEnvProvider } from "~/components/ui";
import { publishPlexServers } from "../plex/plex-api";
import { SmartPrefetchSettings } from "./smart-prefetch";
import { PREFETCH_KEY, parsePrefetchSettings, type PrefetchSettings } from "./smart-prefetch-model";

function Harness({
  managed = false,
  initial,
  persist = vi.fn(async () => {}),
}: {
  managed?: boolean;
  initial?: PrefetchSettings;
  persist?: (patch: Record<string, string>) => Promise<void>;
}) {
  const initialConfig = initial ? { [PREFETCH_KEY]: JSON.stringify(initial) } : {};
  const [savedConfig, setSavedConfig] = useState<Record<string, string>>(initialConfig);
  const [config, setConfig] = useState<Record<string, string>>({
    ...initialConfig,
    unrelated: "draft value",
  });
  const persistConfigPatch = async (patch: Record<string, string>) => {
    await persist(patch);
    setSavedConfig((current) => ({ ...current, ...patch }));
  };
  return (
    <ManagedEnvProvider
      value={
        managed ? { "smart-prefetch.settings": "NZBDAV_CONFIG__SMART_PREFETCH__SETTINGS" } : {}
      }
    >
      <SmartPrefetchSettings
        config={config}
        savedConfig={savedConfig}
        setNewConfig={setConfig}
        persistConfigPatch={persistConfigPatch}
      />
      <output data-testid="config">{JSON.stringify(config)}</output>
      <output data-testid="saved-config">{JSON.stringify(savedConfig)}</output>
    </ManagedEnvProvider>
  );
}
function fakeApi(collection = false, serverEnabled = true) {
  return vi
    .fn<(url: string, init?: RequestInit) => Promise<Response>>()
    .mockImplementation((url, init) => {
      const op = url.split("/api/plex/")[1];
      const request = JSON.parse(typeof init?.body === "string" ? init.body : "{}") as {
        libraryId?: string | null;
      };
      const snapshot = (data: unknown[]) => ({
        data,
        isStale: false,
        lastSuccess: "2026-09-20T00:00:00Z",
        error: null,
      });
      const body = (
        {
          accounts: { accounts: [] },
          servers: { servers: [{ id: "server", name: "Home", enabled: serverEnabled }] },
          libraries: snapshot([{ id: "2", title: "TV", type: "show" }]),
          users: snapshot([{ id: "7", name: "Owner" }]),
          sources: snapshot(
            request.libraryId === "2"
              ? [
                  {
                    serverId: "server",
                    libraryId: "2",
                    kind: collection ? "collection" : "hub",
                    id: collection ? "collection-42" : "home.onDeck",
                    key: collection ? "/library/collections/42/children" : "/hubs/on-deck",
                    title: collection ? "Anime" : "On Deck",
                    type: collection ? "collection" : "show",
                  },
                ]
              : [],
          ),
          preview: {
            items: [
              {
                ratingKey: "42",
                title: "Episode one",
                type: "episode",
                file: "/Plex/episode.mkv",
                mappingStatus: "unmapped",
                mappingReason: "No exact configured path mapping.",
              },
            ],
          },
        } as Record<string, unknown>
      )[op ?? ""];
      if (body) return Promise.resolve(new Response(JSON.stringify(body)));
      if (url.endsWith("/api/prefetch/preview"))
        return Promise.resolve(
          new Response(
            JSON.stringify({
              predictions: [
                {
                  itemId: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                  displayName: "Predicted episode",
                  source: "History",
                  reason: "Next unwatched episode",
                  start: 0,
                  length: 100,
                  fileSize: 1000,
                },
              ],
            }),
          ),
        );
      if (init?.method === "POST")
        return Promise.resolve(
          new Response(JSON.stringify({ status: true, jobs: [], rejected: [] })),
        );
      return Promise.resolve(
        new Response(
          JSON.stringify({
            available: true,
            paused: false,
            initializationError: null,
            lastSuccess: "2026-09-20T00:00:00Z",
            lastError: "History server unavailable",
            jobs: [
              {
                id: "job-1",
                itemId: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                state: "failed",
                trigger: "manual",
                displayName: "Imported episode",
                source: "Manual request",
                reason: "Operator selected media",
                fileSize: 1000,
                committedBytes: 200,
                length: 100,
                priority: 50,
                error: "Provider unavailable",
              },
            ],
          }),
        ),
      );
    });
}
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("Smart Prefetch settings", () => {
  it("does not offer disabled Plex servers as source targets", async () => {
    vi.stubGlobal("fetch", fakeApi(false, false));
    render(<Harness />);

    expect(await screen.findByText(/Connect a Plex server first/)).toBeTruthy();
    expect(screen.queryByRole("option", { name: /Home/ })).toBeNull();
  });

  it("applies only the Smart Prefetch draft and clears its dirty state", async () => {
    vi.stubGlobal("fetch", fakeApi());
    const persist = vi.fn<(patch: Record<string, string>) => Promise<void>>(async () => {});
    render(<Harness persist={persist} />);
    const apply = screen.getByRole("button", { name: "Apply source changes" });
    expect(apply.hasAttribute("disabled")).toBe(true);

    await userEvent.click(screen.getByLabelText("Movies"));
    expect(screen.getByText("Source changes not applied")).toBeTruthy();
    expect(apply.hasAttribute("disabled")).toBe(false);
    await userEvent.click(apply);

    await waitFor(() => expect(persist).toHaveBeenCalledTimes(1));
    const patch = persist.mock.calls[0]?.[0];
    expect(Object.keys(patch ?? {})).toEqual([PREFETCH_KEY]);
    const draft = JSON.parse(screen.getByTestId("config").textContent) as Record<string, unknown>;
    expect(draft["unrelated"]).toBe("draft value");
    expect(await screen.findByText("Source changes applied.")).toBeTruthy();
    await waitFor(() => expect(apply.hasAttribute("disabled")).toBe(true));
  });

  it("keeps source changes dirty and reports a scoped Apply failure", async () => {
    vi.stubGlobal("fetch", fakeApi());
    render(<Harness persist={vi.fn(async () => Promise.reject(new Error("offline")))} />);
    await userEvent.click(screen.getByLabelText("TV episodes"));
    await userEvent.click(screen.getByRole("button", { name: "Apply source changes" }));

    expect(await screen.findByText("Could not apply source changes. Try again.")).toBeTruthy();
    expect(screen.getByText("Source changes not applied")).toBeTruthy();
    expect(
      screen.getByRole("button", { name: "Apply source changes" }).hasAttribute("disabled"),
    ).toBe(false);
  });

  it("makes scoped Apply unavailable when Smart Prefetch is environment-managed", () => {
    vi.stubGlobal("fetch", fakeApi());
    render(<Harness managed />);
    expect(
      screen.getByRole("button", { name: "Apply source changes" }).hasAttribute("disabled"),
    ).toBe(true);
  });

  it("shows essential policy choices and keeps expert controls in a collapsed disclosure", async () => {
    vi.stubGlobal("fetch", fakeApi());
    render(<Harness />);
    expect(screen.getByLabelText("Enable Smart Prefetch")).toBeTruthy();
    expect(screen.getByLabelText("Movies")).toBeTruthy();
    expect(screen.getByLabelText("TV episodes")).toBeTruthy();
    expect(screen.getByLabelText("Daily download budget (GB/day)")).toBeTruthy();
    expect(screen.getByText("Smart defaults are on")).toBeTruthy();
    expect(await screen.findByText("Home connected")).toBeTruthy();
    const details = screen.getByText("Advanced settings").closest("details");
    expect(details?.open).toBe(false);
    expect(details?.contains(screen.getByLabelText("Episodes to queue ahead"))).toBe(true);
    const sources = screen.getByText("Plex libraries and sources").closest("details");
    expect(sources?.open).toBe(false);
    expect(sources?.contains(screen.getByLabelText("Plex source server"))).toBe(true);
    const activity = screen.getByText("Prefetch activity").closest("details");
    expect(activity?.open).toBe(false);
    expect(
      activity?.contains(await screen.findByRole("button", { name: "Preview policies" })),
    ).toBe(true);
    await userEvent.click(screen.getByText("Advanced settings"));
    expect(details?.open).toBe(true);
    await userEvent.click(screen.getByText("Plex libraries and sources"));
    expect(sources?.open).toBe(true);
    await userEvent.click(screen.getByText("Prefetch activity"));
    expect(activity?.open).toBe(true);
  });

  it("marks customized policy and resets defaults without losing identity selections", async () => {
    vi.stubGlobal("fetch", fakeApi());
    const initial = {
      ...parsePrefetchSettings(undefined),
      Enabled: true,
      MaxRetries: 9,
      Users: ["server:7"],
      Sources: [
        {
          ServerId: "server",
          LibraryId: "2",
          Kind: "hub",
          Key: "/hubs/recent",
          Title: "Recent TV",
          Type: "show",
          Enabled: true,
          Limit: 10,
          ExcludedShows: ["42"],
        },
      ],
    };
    render(<Harness initial={initial} />);
    expect(screen.getByText("Customized")).toBeTruthy();
    expect(screen.getByText("Customized policy")).toBeTruthy();
    await userEvent.click(screen.getByText("Advanced settings"));
    await userEvent.click(screen.getByRole("button", { name: "Reset to smart defaults" }));
    const config = JSON.parse(screen.getByTestId("config").textContent) as Record<string, string>;
    const saved = parsePrefetchSettings(config["smart-prefetch.settings"]);
    expect(saved.Enabled).toBe(true);
    expect(saved.MaxRetries).toBe(3);
    expect(saved.Users).toEqual(["server:7"]);
    expect(saved.Sources).toEqual(initial.Sources);
  });

  it("preserves hidden custom values when editing an essential control", async () => {
    vi.stubGlobal("fetch", fakeApi());
    render(
      <Harness
        initial={{ ...parsePrefetchSettings(undefined), MaxBytesPerItem: 123_000_000_000 }}
      />,
    );
    await userEvent.click(screen.getByLabelText("Movies"));
    const config = JSON.parse(screen.getByTestId("config").textContent) as Record<string, string>;
    const saved = parsePrefetchSettings(config["smart-prefetch.settings"]);
    expect(saved.MovieEnabled).toBe(false);
    expect(saved.MaxBytesPerItem).toBe(123_000_000_000);
  });

  it("opens Advanced settings when an advanced value is invalid", async () => {
    vi.stubGlobal("fetch", fakeApi());
    render(<Harness initial={{ ...parsePrefetchSettings(undefined), MaxRetries: 11 }} />);
    expect(
      screen.getByText(
        "Retries after failure (0 means no retries) must be a whole number from 0 to 10.",
      ),
    ).toBeTruthy();
    await waitFor(() =>
      expect(screen.getByText("Advanced settings").closest("details")?.open).toBe(true),
    );
  });

  it("clears a selected source server when a refresh removes it", async () => {
    vi.stubGlobal("fetch", fakeApi());
    render(<Harness />);
    await screen.findByRole("option", { name: "Home" });
    const selector = screen.getByLabelText<HTMLSelectElement>("Plex source server");
    await userEvent.selectOptions(selector, "server");
    expect(selector.value).toBe("server");
    act(() => publishPlexServers([]));
    await waitFor(() => expect(selector.value).toBe(""));
    expect(
      screen.getByRole("button", { name: "Refresh Plex catalogue" }).hasAttribute("disabled"),
    ).toBe(true);
  });
  it("selects real Plex collection records using their owning library media type", async () => {
    vi.stubGlobal("fetch", fakeApi(true));
    render(<Harness />);
    await waitFor(() => expect(screen.getByRole("option", { name: "Home" })).toBeTruthy());
    await userEvent.selectOptions(screen.getByLabelText("Plex source server"), "server");
    await userEvent.click(await screen.findByRole("button", { name: "Show TV sources" }));
    await userEvent.click(screen.getByRole("button", { name: "Show TV collections" }));
    await userEvent.click(await screen.findByLabelText("Enable TV collection Anime"));
    const config = JSON.parse(screen.getByTestId("config").textContent) as Record<string, string>;
    expect(parsePrefetchSettings(config["smart-prefetch.settings"]).Sources[0]).toMatchObject({
      Kind: "collection",
      Type: "show",
      LibraryId: "2",
    });
    await userEvent.click(screen.getByRole("button", { name: "Customize Anime" }));
    await userEvent.click(screen.getByRole("button", { name: "Preview Anime" }));
    expect(await screen.findByText("Episode one")).toBeTruthy();
    expect(screen.getByText(/unmapped.*No exact configured path mapping/)).toBeTruthy();
  });
  it("keeps draft sources and the previous catalogue when a forced refresh partially fails", async () => {
    const base = fakeApi();
    vi.stubGlobal(
      "fetch",
      vi.fn().mockImplementation((url: string, init?: RequestInit) => {
        const request = JSON.parse(typeof init?.body === "string" ? init.body : "{}") as {
          forceRefresh?: boolean;
          libraryId?: string | null;
        };
        if (url.endsWith("/api/plex/sources") && request.forceRefresh && request.libraryId === "2")
          return Promise.resolve(new Response("", { status: 503 }));
        return base(url, init);
      }),
    );
    render(<Harness />);
    await userEvent.selectOptions(await screen.findByLabelText("Plex source server"), "server");
    await userEvent.click(await screen.findByRole("button", { name: "Show TV sources" }));
    await userEvent.click(await screen.findByLabelText("Enable TV source On Deck"));

    await userEvent.click(screen.getByRole("button", { name: "Refresh Plex catalogue" }));

    expect(await screen.findByText(/Catalogue is stale/)).toBeTruthy();
    expect(screen.getByLabelText("Enable TV source On Deck")).toBeTruthy();
    const config = JSON.parse(screen.getByTestId("config").textContent) as Record<string, string>;
    expect(parsePrefetchSettings(config[PREFETCH_KEY]).Sources[0]?.Key).toBe("/hubs/on-deck");
  });
  it("shows saved sources that are absent from the refreshed catalogue", async () => {
    vi.stubGlobal("fetch", fakeApi());
    render(
      <Harness
        initial={{
          ...parsePrefetchSettings(undefined),
          Sources: [
            {
              ServerId: "server",
              LibraryId: "2",
              Kind: "hub",
              Key: "/hubs/missing",
              Title: "Missing hub",
              Type: "show",
              Enabled: false,
              Limit: 25,
              ExcludedShows: [],
            },
          ],
        }}
      />,
    );
    await userEvent.selectOptions(await screen.findByLabelText("Plex source server"), "server");
    await userEvent.click(await screen.findByRole("button", { name: "Show TV sources" }));

    expect(screen.getByText("Saved but not currently available")).toBeTruthy();
    expect(screen.getByLabelText("Enable TV unavailable source Missing hub")).toBeTruthy();
  });
  it("shows policy errors and whole-file coverage, and previews without enqueueing", async () => {
    const fetcher = fakeApi();
    vi.stubGlobal("fetch", fetcher);
    render(<Harness />);
    expect(await screen.findByText("History server unavailable")).toBeTruthy();
    expect(screen.getByText(/200 of 1,000 cached file bytes/)).toBeTruthy();
    expect(screen.getByText(/Imported episode/)).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Preview policies" }));
    expect(await screen.findByText("Predicted episode")).toBeTruthy();
    expect(screen.getByText(/Next unwatched episode/)).toBeTruthy();
    expect(fetcher.mock.calls.some((call) => call[0].endsWith("/operations"))).toBe(false);
  });
  it("edits complete typed controls and preserves scoped users and source exclusions", async () => {
    vi.stubGlobal("fetch", fakeApi());
    render(<Harness />);
    await userEvent.click(screen.getByLabelText("Enable Smart Prefetch"));
    await userEvent.click(screen.getByText("Advanced settings"));
    await userEvent.clear(screen.getByLabelText("Episodes to queue ahead"));
    await userEvent.type(screen.getByLabelText("Episodes to queue ahead"), "3");
    await waitFor(() => expect(screen.getByRole("option", { name: "Home" })).toBeTruthy());
    await userEvent.selectOptions(screen.getByLabelText("Plex source server"), "server");
    await userEvent.selectOptions(screen.getByLabelText("Watching profile"), "server:7");
    await userEvent.click(await screen.findByRole("button", { name: "Show TV sources" }));
    await userEvent.click(await screen.findByLabelText("Enable TV source On Deck"));
    await userEvent.click(screen.getByRole("button", { name: "Customize On Deck" }));
    await userEvent.type(screen.getByLabelText("Excluded shows for On Deck"), "42, 43");
    await userEvent.click(screen.getByRole("button", { name: "Preview On Deck" }));
    expect(await screen.findByText("Episode one")).toBeTruthy();
    const config = JSON.parse(screen.getByTestId("config").textContent) as Record<string, string>;
    const saved = parsePrefetchSettings(config["smart-prefetch.settings"]);
    expect(saved.Enabled).toBe(true);
    expect(saved.MaxQueueAhead).toBe(3);
    expect(saved.Users).toEqual(["server:7"]);
    expect(saved.Sources[0]?.ExcludedShows).toEqual(["42", "43"]);
  });
  it("explains rejected preview candidates and unknown watch-state warnings", async () => {
    const fallback = fakeApi();
    vi.stubGlobal("fetch", (url: string, init?: RequestInit) =>
      url.endsWith("/api/prefetch/preview")
        ? Promise.resolve(
            new Response(
              JSON.stringify({
                warning: "Watched status is unknown for this user.",
                predictions: [
                  {
                    itemId: "00000000-0000-0000-0000-000000000000",
                    displayName: "Unmapped movie",
                    source: "Selected source",
                    reason: "No exact configured path mapping",
                    eligible: false,
                    start: 0,
                    length: 0,
                    fileSize: 0,
                  },
                ],
              }),
            ),
          )
        : fallback(url, init),
    );
    render(<Harness />);
    await screen.findByText(/Imported episode/);
    await userEvent.click(screen.getByRole("button", { name: "Preview policies" }));
    expect(await screen.findByText("Watched status is unknown for this user.")).toBeTruthy();
    expect(screen.getByText("Not eligible for warming")).toBeTruthy();
    expect(screen.queryByText(/0 requested bytes/)).toBeNull();
  });
  it("uses the one bounded queue for bulk warm and retry operations", async () => {
    const fetcher = fakeApi();
    vi.stubGlobal("fetch", fetcher);
    render(<Harness />);
    await screen.findByText("Provider unavailable");
    expect(screen.getByLabelText("Priority for job-1").getAttribute("min")).toBe("-100");
    await userEvent.click(screen.getByRole("button", { name: "Retry job-1" }));
    expect(fetcher).toHaveBeenCalledWith(
      expect.stringContaining("/api/prefetch/operations"),
      expect.objectContaining({ body: JSON.stringify({ operation: "retry", jobId: "job-1" }) }),
    );
    await userEvent.type(
      screen.getByLabelText("Imported media IDs (up to 32)"),
      "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
    );
    await userEvent.click(screen.getByRole("button", { name: "Warm selected media" }));
    expect(fetcher).toHaveBeenCalledWith(
      expect.stringContaining("/api/prefetch/operations"),
      expect.objectContaining({ body: expect.stringContaining('"operation":"warm"') as unknown }),
    );
  });
  it("keeps environment-owned policy controls read-only", () => {
    vi.stubGlobal("fetch", fakeApi());
    render(<Harness managed />);
    expect(screen.getByLabelText("Enable Smart Prefetch").closest("fieldset")?.disabled).toBe(true);
  });
  it("shows runtime metadata failure and disables warming mutations", async () => {
    const fallback = fakeApi();
    vi.stubGlobal("fetch", (url: string, init?: RequestInit) =>
      url.endsWith("/api/prefetch")
        ? Promise.resolve(
            new Response(
              JSON.stringify({
                available: true,
                healthy: false,
                jobs: [],
                runtimeError: "Warming paused after metadata failure; playback remains available.",
              }),
            ),
          )
        : fallback(url, init),
    );
    render(<Harness />);
    expect(
      await screen.findByText("Warming paused after metadata failure; playback remains available."),
    ).toBeTruthy();
    expect(
      screen.getByRole("button", { name: "Sync Plex policies now" }).closest("fieldset")?.disabled,
    ).toBe(true);
  });
  it("shows per-item bulk results including deduplication and rejection reasons", async () => {
    const fallback = fakeApi();
    vi.stubGlobal("fetch", (url: string, init?: RequestInit) =>
      url.endsWith("/api/prefetch/operations") && init?.method === "POST"
        ? Promise.resolve(
            new Response(
              JSON.stringify({
                outcomes: [
                  {
                    itemId: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                    status: "deduplicated",
                    jobId: "existing",
                    start: 0,
                    length: 0,
                  },
                  {
                    itemId: "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                    status: "rejected",
                    start: 0,
                    length: 0,
                    reason: "Not an available imported streamable file.",
                  },
                ],
              }),
            ),
          )
        : fallback(url, init),
    );
    render(<Harness />);
    await screen.findByText(/Imported episode/);
    await userEvent.type(
      screen.getByLabelText("Imported media IDs (up to 32)"),
      "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa,bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
    );
    await userEvent.click(screen.getByRole("button", { name: "Warm selected media" }));
    expect(await screen.findByText(/deduplicated.*existing/)).toBeTruthy();
    expect(screen.getByText(/Not an available imported streamable file/)).toBeTruthy();
  });
});
