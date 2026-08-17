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

function loaderArgs(path = "/health") {
  return {
    request: new Request(`http://localhost${path}`),
  } as Parameters<typeof loader>[0];
}

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
      totalCount: 1,
    });
    getConfigMock.mockResolvedValueOnce([
      { configName: "repair.enable", configValue: "TRUE" },
    ]);

    await expect(loader(loaderArgs())).resolves.toEqual({
      uncheckedCount: 12,
      queueItems,
      historyStats,
      historyItems,
      historyTotalCount: 1,
      historyPage: 1,
      historyPageSize: 25,
      historyFilter: "all",
      isEnabled: true,
    });
    expect(getHealthCheckQueueMock).toHaveBeenCalledWith(30);
    expect(getHealthCheckHistoryMock).toHaveBeenCalledWith({
      page: 1,
      pageSize: 25,
      repairStatus: "deleted,repaired",
    });
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
      totalCount: 0,
    });
    getConfigMock
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce([
        { configName: "repair.enable", configValue: "false" },
      ]);

    await expect(loader(loaderArgs())).resolves.toMatchObject({ isEnabled: false });
    await expect(loader(loaderArgs())).resolves.toMatchObject({ isEnabled: false });
  });

  it("uses URL-backed paging and repair-status filters", async () => {
    getHealthCheckQueueMock.mockResolvedValue({ uncheckedCount: 0, items: [] });
    getHealthCheckHistoryMock.mockResolvedValue({ stats: [], items: [], totalCount: 0 });
    getConfigMock.mockResolvedValue([]);

    await expect(loader(loaderArgs("/health?page=3&pageSize=50&status=deleted"))).resolves.toMatchObject({
      historyPage: 3,
      historyPageSize: 50,
      historyFilter: "deleted",
    });
    expect(getHealthCheckHistoryMock).toHaveBeenCalledWith({
      page: 3,
      pageSize: 50,
      repairStatus: "deleted",
    });
  });

  it("surfaces backend failures instead of returning partial health data", async () => {
    getHealthCheckQueueMock.mockRejectedValueOnce(new Error("queue unavailable"));
    getHealthCheckHistoryMock.mockResolvedValueOnce({ stats: [], items: [], totalCount: 0 });
    getConfigMock.mockResolvedValueOnce([]);

    await expect(loader(loaderArgs())).rejects.toThrow("queue unavailable");
  });
});
