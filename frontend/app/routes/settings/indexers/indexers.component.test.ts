// @vitest-environment jsdom
/* global HTMLInputElement */
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createElement, useState, type Dispatch, type SetStateAction } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { IndexersSettings } from "./indexers";

const fetchMock = vi.fn<typeof fetch>();

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

const baseConfig: Record<string, string> = {
  "indexers.instances": '{"Indexers":[]}',
  "profiles.instances": '{"Profiles":[]}',
  "api.user-agent": "",
  "api.search-user-agent": "",
  "search.exclude-patterns": "",
  "search.exclude-sync-urls": "",
  "search.exclude-sync-refresh-minutes": "720",
  "prowlarr.url": "http://prowlarr:9696",
  "prowlarr.api-key": "prowlarr-secret",
  "prowlarr.sync-enabled": "false",
  "prowlarr.sync-interval-minutes": "60",
};

const syncedStatus = {
  status: true,
  configured: true,
  syncEnabled: true,
  indexersEnvironmentManaged: false,
  profilesEnvironmentManaged: false,
  lastAttemptAt: 1_800_000_000,
  lastSuccessAt: 1_800_000_000,
  error: null,
  remoteIndexerCount: 3,
  added: 2,
  updated: 1,
  removed: 0,
  skipped: 1,
};

function urlOf(input: RequestInfo | URL): string {
  if (typeof input === "string") return input;
  if (input instanceof URL) return input.href;
  return input.url;
}

function mockRoutes(overrides: Record<string, unknown> = {}) {
  fetchMock.mockImplementation((input) => {
    const url = urlOf(input);
    if (url.includes("/settings/exclude-sync")) return Promise.resolve(jsonResponse({ urls: [] }));
    if (url.includes("/api/test-prowlarr-connection")) {
      return Promise.resolve(
        jsonResponse(overrides["testConnection"] ?? { status: true, connected: true }),
      );
    }
    if (url.includes("/api/prowlarr-sync")) {
      return Promise.resolve(jsonResponse(overrides["sync"] ?? syncedStatus));
    }
    return Promise.reject(new Error(`unexpected fetch: ${url}`));
  });
}

type HarnessProps = {
  initial?: Record<string, string>;
  savedConfig?: Record<string, string>;
  onSyncedConfig?: (patch: Record<string, string>) => void;
  onConfigChange?: Dispatch<SetStateAction<Record<string, string>>>;
};

function Harness({ initial, savedConfig, onSyncedConfig, onConfigChange }: HarnessProps) {
  const [config, setConfig] = useState(initial ?? baseConfig);
  const setNewConfig: Dispatch<SetStateAction<Record<string, string>>> = (update) => {
    onConfigChange?.(update);
    setConfig(update);
  };
  return createElement(IndexersSettings, {
    config,
    setNewConfig,
    savedConfig: savedConfig ?? initial ?? baseConfig,
    ...(onSyncedConfig ? { onSyncedConfig } : {}),
  });
}

function prowlarrCard(): HTMLElement {
  const section = screen.getByText("Prowlarr pull sync").closest("section");
  if (!section) throw new Error("Prowlarr card not found");
  return section;
}

beforeEach(() => {
  vi.stubGlobal("fetch", fetchMock);
  // jsdom does not implement <dialog> modal behavior.
  if (!HTMLDialogElement.prototype.showModal) {
    HTMLDialogElement.prototype.showModal = function (this: HTMLDialogElement) {
      this.open = true;
    };
    HTMLDialogElement.prototype.close = function (this: HTMLDialogElement) {
      this.open = false;
    };
  }
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

describe("IndexersSettings Prowlarr pull sync", () => {
  it("renders connection fields and loads the last sync status", async () => {
    mockRoutes();
    render(createElement(Harness));

    expect(screen.getByLabelText("Prowlarr URL")).toHaveProperty("value", "http://prowlarr:9696");
    expect(screen.getByLabelText("Prowlarr API key")).toHaveProperty("value", "prowlarr-secret");

    await waitFor(() => {
      expect(prowlarrCard().textContent).toContain("Last synced");
    });
    expect(prowlarrCard().textContent).toContain("3 Prowlarr indexers");
    expect(prowlarrCard().textContent).toContain("2 added");
  });

  it("shows a failed sync status with its error", async () => {
    mockRoutes({
      sync: {
        ...syncedStatus,
        lastSuccessAt: null,
        error: "Prowlarr returned HTTP 503.",
      },
    });
    render(createElement(Harness));

    await waitFor(() => {
      expect(prowlarrCard().textContent).toContain("Last sync failed");
    });
    expect(prowlarrCard().textContent).toContain("HTTP 503");
  });

  it("tests the connection with the entered URL and API key", async () => {
    mockRoutes();
    const user = userEvent.setup();
    render(createElement(Harness));

    const card = prowlarrCard();
    await user.click(within(card).getByRole("button", { name: "Test Connection" }));

    await waitFor(() => {
      expect(card.textContent).toContain("Prowlarr connection test successful.");
    });
    const testCall = fetchMock.mock.calls.find(([input]) =>
      urlOf(input).includes("/api/test-prowlarr-connection"),
    );
    expect(testCall).toBeDefined();
    const body = testCall![1]?.body as FormData;
    expect(body.get("url")).toBe("http://prowlarr:9696");
    expect(body.get("apiKey")).toBe("prowlarr-secret");
  });

  it("reports a failed connection test", async () => {
    mockRoutes({
      testConnection: { status: true, connected: false, error: "Authentication failed" },
    });
    const user = userEvent.setup();
    render(createElement(Harness));

    const card = prowlarrCard();
    await user.click(within(card).getByRole("button", { name: "Test Connection" }));

    await waitFor(() => {
      expect(card.textContent).toContain("Authentication failed");
    });
  });

  it("syncs on demand and applies the returned indexer and profile config", async () => {
    const indexerJson =
      '{"Indexers":[{"Name":"Managed","Url":"http://prowlarr:9696/7/api","ApiKey":"k","Enabled":true,"ProwlarrIndexerId":7}]}';
    const profileJson = '{"Profiles":[]}';
    mockRoutes({
      sync: { ...syncedStatus, indexerConfigJson: indexerJson, profileConfigJson: profileJson },
    });
    const onSyncedConfig = vi.fn();
    const user = userEvent.setup();
    render(createElement(Harness, { onSyncedConfig }));

    const card = prowlarrCard();
    await user.click(within(card).getByRole("button", { name: "Sync now" }));

    await waitFor(() => {
      expect(onSyncedConfig).toHaveBeenCalledWith({
        "indexers.instances": indexerJson,
        "profiles.instances": profileJson,
      });
    });
    const syncCalls = fetchMock.mock.calls.filter(
      ([input, init]) => urlOf(input).includes("/api/prowlarr-sync") && init?.method === "POST",
    );
    expect(syncCalls).toHaveLength(1);
  });

  it("disables manual sync while settings have unsaved changes", async () => {
    mockRoutes();
    render(
      createElement(Harness, {
        savedConfig: { ...baseConfig, "prowlarr.url": "http://old-prowlarr:9696" },
      }),
    );

    const card = prowlarrCard();
    await waitFor(() => {
      expect(card.textContent).toContain("Last synced");
    });
    expect(within(card).getByRole("button", { name: "Sync now" })).toHaveProperty("disabled", true);
  });

  it("disables manual sync when indexers are environment-managed", async () => {
    mockRoutes({
      sync: { ...syncedStatus, indexersEnvironmentManaged: true },
    });
    render(createElement(Harness));

    const card = prowlarrCard();
    await waitFor(() => {
      expect(card.textContent).toContain("managed by the environment");
    });
    expect(within(card).getByRole("button", { name: "Sync now" })).toHaveProperty("disabled", true);
  });

  it("updates Prowlarr settings through the shared config state", async () => {
    mockRoutes();
    const onConfigChange = vi.fn();
    const user = userEvent.setup();
    render(createElement(Harness, { onConfigChange }));

    await user.click(screen.getByRole("checkbox", { name: "Automatically sync" }));
    expect(onConfigChange).toHaveBeenCalledWith(
      expect.objectContaining({
        "prowlarr.sync-enabled": "true",
      }),
    );

    const url = screen.getByLabelText<HTMLInputElement>("Prowlarr URL");
    await user.clear(url);
    await user.type(url, "http://prowlarr:9696/base");
    expect(onConfigChange).toHaveBeenLastCalledWith(
      expect.objectContaining({
        "prowlarr.url": "http://prowlarr:9696/base",
      }),
    );

    const interval = screen.getByLabelText<HTMLInputElement>(/Sync every/);
    await user.clear(interval);
    await user.type(interval, "30");
    expect(onConfigChange).toHaveBeenLastCalledWith(
      expect.objectContaining({
        "prowlarr.sync-interval-minutes": "30",
      }),
    );
  });

  it("flags an invalid Prowlarr URL and blocks the connection test", async () => {
    mockRoutes();
    const user = userEvent.setup();
    render(createElement(Harness));

    const url = screen.getByLabelText<HTMLInputElement>("Prowlarr URL");
    await user.clear(url);
    await user.type(url, "https://user:secret@prowlarr:9696");

    expect(url.className).toContain("input-error");
    expect(within(prowlarrCard()).getByRole("button", { name: "Test Connection" })).toHaveProperty(
      "disabled",
      true,
    );
  });

  it("marks Prowlarr-managed indexers and keeps their remote ID when edited", async () => {
    mockRoutes();
    const managedConfig = {
      ...baseConfig,
      "indexers.instances": JSON.stringify({
        Indexers: [
          {
            Name: "Managed One",
            Url: "http://prowlarr:9696/7/api",
            ApiKey: "masked-key",
            Enabled: true,
            ProwlarrIndexerId: 7,
          },
        ],
      }),
    };
    let latestConfig: Record<string, string> = managedConfig;
    const user = userEvent.setup();
    render(
      createElement(Harness, {
        initial: managedConfig,
        onConfigChange: (update) => {
          latestConfig = typeof update === "function" ? update(latestConfig) : update;
        },
      }),
    );

    expect(screen.getByText("Prowlarr")).toBeTruthy();

    await user.click(screen.getByTitle("Edit Indexer"));
    expect(screen.getByText(/Prowlarr manages this indexer's name/)).toBeTruthy();

    await user.click(screen.getByRole("button", { name: "Save Indexer" }));
    const saved = JSON.parse(latestConfig["indexers.instances"] ?? "{}") as {
      Indexers: Array<{ ProwlarrIndexerId?: number; Name?: string }>;
    };
    expect(saved.Indexers[0]?.ProwlarrIndexerId).toBe(7);
    expect(saved.Indexers[0]?.Name).toBe("Managed One");
  });
});
