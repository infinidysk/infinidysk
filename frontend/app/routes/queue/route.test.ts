import { beforeEach, describe, expect, it, vi } from "vitest";
import { loader } from "./route";

const { getConfigMock, getHistoryMock, getQueueMock } = vi.hoisted(() => ({
  getConfigMock: vi.fn(),
  getHistoryMock: vi.fn(),
  getQueueMock: vi.fn(),
}));

vi.mock("~/clients/backend-client.server", () => ({
  backendClient: {
    getConfig: getConfigMock,
    getHistory: getHistoryMock,
    getQueue: getQueueMock,
  },
}));

vi.mock("./components/history-table/history-table", () => ({
  HistoryTable: vi.fn(),
}));

vi.mock("./components/queue-table/queue-table", () => ({
  QueueTable: vi.fn(),
}));

vi.mock("./controllers/events-controller", () => ({
  useHistoryEvents: vi.fn(),
  useQueueEvents: vi.fn(),
}));

vi.mock("./controllers/websocket-controller", () => ({
  useQueueHistoryWebsocket: vi.fn(),
}));

vi.mock("./controllers/nzb-upload-controller", () => ({
  useUploadController: vi.fn(),
}));

vi.mock("./controllers/dropzone-controller", () => ({
  useQueueDropzone: vi.fn(),
}));

vi.mock("~/components/ui", () => ({
  Alert: vi.fn(),
}));

vi.mock("~/auth/authorization", () => ({
  useIsReadOnly: vi.fn(),
}));

function loaderRequest(search = ""): Parameters<typeof loader>[0] {
  return {
    request: new Request(`http://localhost/queue${search}`),
  } as Parameters<typeof loader>[0];
}

describe("queue route loader", () => {
  beforeEach(() => {
    getConfigMock.mockReset();
    getHistoryMock.mockReset();
    getQueueMock.mockReset();
  });

  it("loads the requested queue and history pages with configured categories", async () => {
    const queueSlots = [{ nzo_id: "queue-1" }, { nzo_id: "queue-2" }];
    const fetchedQueueSlots = [{ nzo_id: "queue-before" }, ...queueSlots, { nzo_id: "queue-after" }];
    const historySlots = [{ nzo_id: "history-1" }];
    getQueueMock.mockResolvedValueOnce({ slots: fetchedQueueSlots, noofslots: 30 });
    getHistoryMock.mockResolvedValueOnce({ slots: historySlots, noofslots: 700 });
    getConfigMock.mockResolvedValueOnce([
      { configName: "api.categories", configValue: "tv, movies" },
      { configName: "api.manual-category", configValue: "anime" },
    ]);

    const result = await loader(loaderRequest("?qp=2&hp=3&qps=25&hps=250"));

    expect(getQueueMock).toHaveBeenCalledWith(27, 24, {
      search: "", category: "", status: "", sort: undefined, direction: undefined,
    });
    expect(getHistoryMock).toHaveBeenCalledWith(250, 500, {
      search: "", category: "", status: "", sort: undefined, direction: undefined,
    });
    expect(getConfigMock).toHaveBeenCalledWith([
      "api.categories",
      "api.manual-category",
    ]);
    expect(result).toEqual({
      queueSlots: fetchedQueueSlots.slice(1),
      previousQueueSlot: fetchedQueueSlots[0],
      nextQueueSlot: undefined,
      historySlots,
      totalQueueCount: 30,
      totalHistoryCount: 700,
      categories: ["anime", "tv", "movies"],
      manualCategory: "anime",
      queuePage: 2,
      historyPage: 3,
      queuePageSize: 25,
      historyPageSize: 250,
      queueParams: { query: "", category: "", status: "", sort: null, direction: null },
      historyParams: { query: "", category: "", status: "", sort: null, direction: null },
    });
  });

  it("normalizes invalid pagination and uses safe response defaults", async () => {
    getQueueMock.mockResolvedValueOnce(null);
    getHistoryMock.mockResolvedValueOnce(undefined);
    getConfigMock.mockResolvedValueOnce([]);

    const result = await loader(loaderRequest("?qp=0&hp=invalid&qps=10&hps=999"));

    expect(getQueueMock).toHaveBeenCalledWith(101, 0, {
      search: "", category: "", status: "", sort: undefined, direction: undefined,
    });
    expect(getHistoryMock).toHaveBeenCalledWith(100, 0, {
      search: "", category: "", status: "", sort: undefined, direction: undefined,
    });
    expect(result).toMatchObject({
      queueSlots: [],
      historySlots: [],
      totalQueueCount: 0,
      totalHistoryCount: 0,
      categories: ["uncategorized", "audio", "software", "tv", "movies"],
      manualCategory: "uncategorized",
      queuePage: 1,
      historyPage: 1,
      queuePageSize: 100,
      historyPageSize: 100,
    });
  });

  it("passes independent queue and history filters to the server", async () => {
    getQueueMock.mockResolvedValueOnce({ slots: [], noofslots: 0 });
    getHistoryMock.mockResolvedValueOnce({ slots: [], noofslots: 0 });
    getConfigMock.mockResolvedValueOnce([]);

    await loader(loaderRequest("?qq=show&qcat=tv&qstatus=Paused&qsort=size:desc&hq=film&hcat=movies&hstatus=Failed&hsort=completed:asc"));

    expect(getQueueMock).toHaveBeenCalledWith(101, 0, {
      search: "show", category: "tv", status: "Paused", sort: "size", direction: "desc",
    });
    expect(getHistoryMock).toHaveBeenCalledWith(100, 0, {
      search: "film", category: "movies", status: "Failed", sort: "completed", direction: "asc",
    });
  });

  it("falls back to a non-empty manual category and filters empty category segments", async () => {
    getQueueMock.mockResolvedValueOnce({ slots: [], noofslots: 0 });
    getHistoryMock.mockResolvedValueOnce({ slots: [], noofslots: 0 });
    getConfigMock.mockResolvedValueOnce([
      { configName: "api.categories", configValue: ",tv,, movies," },
      { configName: "api.manual-category", configValue: "" },
    ]);

    const result = await loader(loaderRequest());

    expect(result.manualCategory).toBe("uncategorized");
    expect(result.categories).toEqual(["uncategorized", "tv", "movies"]);
    expect(result.categories).not.toContain("");
  });

  it("falls back to a non-empty manual category when it is whitespace-only", async () => {
    getQueueMock.mockResolvedValueOnce({ slots: [], noofslots: 0 });
    getHistoryMock.mockResolvedValueOnce({ slots: [], noofslots: 0 });
    getConfigMock.mockResolvedValueOnce([
      { configName: "api.categories", configValue: "tv" },
      { configName: "api.manual-category", configValue: "   " },
    ]);

    const result = await loader(loaderRequest());

    expect(result.manualCategory).toBe("uncategorized");
    expect(result.categories).toEqual(["uncategorized", "tv"]);
  });

  it("surfaces backend failures instead of returning partial queue data", async () => {
    getQueueMock.mockRejectedValueOnce(new Error("queue unavailable"));
    getHistoryMock.mockResolvedValueOnce({ slots: [], noofslots: 0 });
    getConfigMock.mockResolvedValueOnce([]);

    await expect(loader(loaderRequest())).rejects.toThrow("queue unavailable");
  });
});
