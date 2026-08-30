import { beforeEach, describe, expect, it, vi } from "vitest";
import type { WebSocketServer } from "ws";

const initialize = vi.fn();

vi.stubEnv("SESSION_KEY", "test-session-key");
vi.stubEnv("BACKEND_URL", "http://127.0.0.1:9");

vi.mock("./websocket.server", () => ({
  websocketServer: {
    initialize,
  },
}));

describe("server bundle runtime handshake", () => {
  beforeEach(() => {
    initialize.mockReset();
  });

  it("passes the installed key into websocket initialization", async () => {
    const { configureRuntime, initializeWebsocketServer } = await import("./app");
    configureRuntime({ frontendBackendApiKey: "bundle-test-key" });
    const fakeWss = {} as WebSocketServer;
    initializeWebsocketServer(fakeWss);
    expect(initialize).toHaveBeenCalledWith(fakeWss, {
      backendApiKey: "bundle-test-key",
    });
  });
});
