import { beforeEach, describe, expect, it, vi } from "vitest";
import { loader } from "./route";

const {
  getConfigMock,
  getHealthCheckHistoryMock,
  getHealthCheckQueueMock,
} = vi.hoisted(() => ({
  getConfigMock: vi.fn(),
  getHealthCheckHistoryMock: vi.fn(),
  getHealthCheckQueueMock: vi.fn(),
}));

vi.mock("~/clients/backend-client.server", () => ({
  backendClient: {
    getConfig: getConfigMock,
    getHealthCheckHistory: getHealthCheckHistoryMock,
    getHealthCheckQueue: getHealthCheckQueueMock,
  },
}));

vi.mock("./components/health-table/health-table", () => ({
  HealthTable: vi.fn(),
}));

vi.mock("./components/health-stats/health-stats", () => ({
  HealthStats: vi.fn(),
}));

vi.mock("~/utils/shared-websocket", () => ({
  useWebsocketTopics: vi.fn(),
}));

vi.mock("~/components/ui", () => ({
  Alert: vi.fn(),
  Icon: vi.fn(),
}));

vi.mock("./health-queue-state", () => ({
  completeHealthCheck: vi.fn(),
}));

describe("health route loader", () => {
  beforeEach(() => {
    getConfigMock.mockReset();
    getHealthCheckHistoryMock.mockReset();
    getHealthCheckQueueMock.mockReset();
  });

  it("combines the health queue, history, and enabled setting", async () => {
    const queueItems = [{ id: "queue-1", name: "Example" }];
    const historyStats = [{ result: 0, repairStatus: 0, count: 4 }];
    const historyItems = [{ id: "history-1", path: "/view/example.mkv" }];
    getHealthCheckQueueMock.mockResolvedValueOnce({
      uncheckedCount: 12,
      items: queueItems,
    });
    getHealthCheckHistoryMock.mockResolvedValueOnce({
      stats: historyStats,
      items: historyItems,
    });
    getConfigMock.mockResolvedValueOnce([
      { configName: "repair.enable", configValue: "TRUE" },
    ]);

    await expect(loader()).resolves.toEqual({
      uncheckedCount: 12,
      queueItems,
      historyStats,
      historyItems,
      isEnabled: true,
    });
    expect(getHealthCheckQueueMock).toHaveBeenCalledWith(30);
    expect(getHealthCheckHistoryMock).toHaveBeenCalledOnce();
    expect(getConfigMock).toHaveBeenCalledWith(["repair.enable"]);
  });

  it("reports health checks disabled when the setting is absent or false", async () => {
    getHealthCheckQueueMock.mockResolvedValue({
      uncheckedCount: 0,
      items: [],
    });
    getHealthCheckHistoryMock.mockResolvedValue({
      stats: [],
      items: [],
    });
    getConfigMock
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce([
        { configName: "repair.enable", configValue: "false" },
      ]);

    await expect(loader()).resolves.toMatchObject({ isEnabled: false });
    await expect(loader()).resolves.toMatchObject({ isEnabled: false });
  });

  it("surfaces backend failures instead of returning partial health data", async () => {
    getHealthCheckQueueMock.mockRejectedValueOnce(new Error("queue unavailable"));
    getHealthCheckHistoryMock.mockResolvedValueOnce({ stats: [], items: [] });
    getConfigMock.mockResolvedValueOnce([]);

    await expect(loader()).rejects.toThrow("queue unavailable");
  });
});
