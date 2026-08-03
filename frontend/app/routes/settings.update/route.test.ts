import { beforeEach, describe, expect, it, vi } from "vitest";
import { action } from "./route";

const { updateConfigMock } = vi.hoisted(() => ({
  updateConfigMock: vi.fn(),
}));

vi.mock("~/clients/backend-client.server", () => ({
  backendClient: {
    updateConfig: updateConfigMock,
  },
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
  });

  it("updates every submitted setting and returns the saved values", async () => {
    updateConfigMock.mockResolvedValueOnce(true);
    const request = configRequest(JSON.stringify({
      "repair.enable": "true",
      "api.manual-category": "movies",
    }));

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

    await expect(
      action({ request } as Parameters<typeof action>[0]),
    ).rejects.toBeInstanceOf(SyntaxError);
    expect(updateConfigMock).not.toHaveBeenCalled();
  });

  it("surfaces backend update failures", async () => {
    updateConfigMock.mockRejectedValueOnce(new Error("backend unavailable"));
    const request = configRequest(JSON.stringify({ "repair.enable": "false" }));

    await expect(
      action({ request } as Parameters<typeof action>[0]),
    ).rejects.toThrow("backend unavailable");
  });
});
