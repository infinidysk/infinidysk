import { beforeEach, describe, expect, it, vi } from "vitest";

const {
  completeSetupWizardMock,
  getConfigMock,
  getSessionUserMock,
  getSetupWizardStateMock,
  skipSetupWizardMock,
} = vi.hoisted(() => ({
  completeSetupWizardMock: vi.fn(),
  getConfigMock: vi.fn(),
  getSessionUserMock: vi.fn(),
  getSetupWizardStateMock: vi.fn(),
  skipSetupWizardMock: vi.fn(),
}));

vi.mock("~/clients/backend-client.server", () => {
  class BackendApiError extends Error {}
  return {
    BackendApiError,
    backendClient: {
      completeSetupWizard: completeSetupWizardMock,
      getConfig: getConfigMock,
      getSetupWizardState: getSetupWizardStateMock,
      skipSetupWizard: skipSetupWizardMock,
    },
  };
});

vi.mock("~/auth/authentication.server", () => ({
  IS_FRONTEND_AUTH_DISABLED: false,
  getSessionUser: getSessionUserMock,
}));

import { action, loader } from "./route";

const state = {
  status: true,
  currentVersion: 1,
  recordedVersion: null,
  disposition: null,
  setupRequired: true,
  ingestionMethods: [],
  updatedAt: null,
  mainDatabaseProvider: "sqlite" as const,
  mainDatabaseBackupSupported: true,
};

beforeEach(() => {
  completeSetupWizardMock.mockReset();
  getConfigMock.mockReset();
  getSessionUserMock.mockReset();
  getSetupWizardStateMock.mockReset();
  skipSetupWizardMock.mockReset();
  getSessionUserMock.mockResolvedValue({ username: "admin", role: "admin" });
});

describe("setup route loader", () => {
  it("loads effective values, managed environment names, and a safe return path", async () => {
    getSetupWizardStateMock.mockResolvedValue(state);
    getConfigMock.mockResolvedValue([
      {
        configName: "api.import-strategy",
        configValue: "strm",
        environmentVariableName: "NZBDAV_CONFIG__API__IMPORT_STRATEGY",
      },
    ]);

    const result = await loader({
      request: new Request("http://localhost/setup?returnTo=%2Fqueue%3Fpage%3D2"),
    } as Parameters<typeof loader>[0]);

    expect(result.config["api.import-strategy"]).toBe("strm");
    expect(result.managedEnv).toEqual({
      "api.import-strategy": "NZBDAV_CONFIG__API__IMPORT_STRATEGY",
    });
    expect(result.returnTo).toBe("/queue?page=2");
  });
});

describe("setup route action", () => {
  it("submits the reviewed configuration for an admin", async () => {
    completeSetupWizardMock.mockResolvedValue({
      status: true,
      restartRequired: true,
      changedConfigKeys: ["api.import-strategy", "usenet.segment-cache.enabled"],
    });
    const form = new FormData();
    form.set("intent", "complete");
    form.set("strategy", "symlinks");
    form.set("ingestionMethods", '["manual"]');
    form.set("config", '{"rclone.mount-dir":"/mnt/nzbdav"}');

    const result = await action({
      request: new Request("http://localhost/setup", { method: "POST", body: form }),
    } as Parameters<typeof action>[0]);

    expect(result).toMatchObject({ ok: true, intent: "complete", restartRequired: true });
    expect(completeSetupWizardMock).toHaveBeenCalledWith({
      strategy: "symlinks",
      ingestionMethods: ["manual"],
      config: { "rclone.mount-dir": "/mnt/nzbdav" },
    });
  });

  it("records an explicit skip", async () => {
    skipSetupWizardMock.mockResolvedValue(true);
    const form = new FormData();
    form.set("intent", "skip");

    await expect(
      action({
        request: new Request("http://localhost/setup", { method: "POST", body: form }),
      } as Parameters<typeof action>[0]),
    ).resolves.toEqual({ ok: true, intent: "skip" });
  });

  it("rejects read-only users before calling the backend", async () => {
    getSessionUserMock.mockResolvedValue({ username: "reader", role: "readonly" });
    const form = new FormData();
    form.set("intent", "skip");

    await expect(
      action({
        request: new Request("http://localhost/setup", { method: "POST", body: form }),
      } as Parameters<typeof action>[0]),
    ).resolves.toEqual({
      ok: false,
      error: "Administrator access is required to change setup.",
    });
    expect(skipSetupWizardMock).not.toHaveBeenCalled();
  });
});
