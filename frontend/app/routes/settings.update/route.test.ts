import { beforeEach, describe, expect, it, vi } from "vitest";
import { action } from "./route";

const { invalidateProxySettingsCacheMock, updateConfigMock } = vi.hoisted(() => ({
  invalidateProxySettingsCacheMock: vi.fn(),
  updateConfigMock: vi.fn(),
}));

vi.mock("~/clients/backend-client.server", () => ({
  backendClient: {
    updateConfig: updateConfigMock,
  },
}));

vi.mock("../../../server/configured-action-origin", () => ({
  invalidateProxySettingsCache: invalidateProxySettingsCacheMock,
}));

function configRequest(config: string): Request {
  const formData = new FormData();
  formData.set("config", config);
  return new Request("http://localhost/settings/update", {
    method: "POST",
    body: formData,
  });
}

describe("settings.update route action", () => {
  beforeEach(() => {
    updateConfigMock.mockReset();
    invalidateProxySettingsCacheMock.mockReset();
  });

  it("updates every submitted setting and returns the saved values", async () => {
    updateConfigMock.mockResolvedValueOnce(true);
    const request = configRequest(
      JSON.stringify({
        "repair.enable": "true",
        "api.manual-category": "movies",
      }),
    );

    const result = await action({ request } as Parameters<typeof action>[0]);

    expect(updateConfigMock).toHaveBeenCalledWith([
      { configName: "repair.enable", configValue: "true" },
      { configName: "api.manual-category", configValue: "movies" },
    ]);
    expect(result).toEqual({
      config: {
        "repair.enable": "true",
        "api.manual-category": "movies",
      },
    });
  });

  it("rejects malformed configuration without updating the backend", async () => {
    const request = configRequest("{not-json");

    await expect(action({ request } as Parameters<typeof action>[0])).rejects.toThrow(
      "Config payload is not valid JSON.",
    );
    expect(updateConfigMock).not.toHaveBeenCalled();
  });

  it("rejects non-string config values without updating the backend", async () => {
    const request = configRequest(JSON.stringify({ "repair.enable": true }));

    await expect(action({ request } as Parameters<typeof action>[0])).rejects.toThrow(
      "string keys to string values",
    );
    expect(updateConfigMock).not.toHaveBeenCalled();
  });

  it("surfaces backend update failures", async () => {
    updateConfigMock.mockRejectedValueOnce(new Error("backend unavailable"));
    const request = configRequest(JSON.stringify({ "repair.enable": "false" }));

    await expect(action({ request } as Parameters<typeof action>[0])).rejects.toThrow(
      "backend unavailable",
    );
  });

  it.each(["general.base-url", "general.trust-proxy"])(
    "invalidates runtime proxy settings after changing %s",
    async (configKey) => {
      updateConfigMock.mockResolvedValueOnce(true);

      await action({
        request: configRequest(JSON.stringify({ [configKey]: "true" })),
      } as Parameters<typeof action>[0]);

      expect(invalidateProxySettingsCacheMock).toHaveBeenCalledOnce();
    },
  );
});
