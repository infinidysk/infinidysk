import { describe, expect, it, vi } from "vitest";
import { admitAndForwardBackendRequest } from "./backend-proxy-admission";
import { isFilesBackendMutation, isReadOnlyDeniedBackendMutation } from "./proxy-path";

function handlers(overrides: Partial<Parameters<typeof admitAndForwardBackendRequest>[1]> = {}) {
  return {
    isAuthenticated: vi.fn(() => Promise.resolve(true)),
    injectApiKey: vi.fn(() => Promise.resolve()),
    getRole: vi.fn(() => Promise.resolve<string | null>("admin")),
    rejectMetrics: vi.fn(),
    rejectReadOnlyMutation: vi.fn(),
    observeRclone: vi.fn(),
    forward: vi.fn(),
    ...overrides,
  };
}

describe("backend proxy admission", () => {
  it("rejects read-only preview requests while allowing administrators to forward them with a key", async () => {
    const path = "/api/delete-webdav-item-preview";
    const readonly = handlers({ getRole: vi.fn(() => Promise.resolve("readonly")) });
    const request = {
      requiresMetricsAuthentication: false,
      isReadOnlyMutation: isReadOnlyDeniedBackendMutation("GET", path),
      userAgent: undefined,
    };
    await admitAndForwardBackendRequest(request, readonly);
    expect(readonly.rejectReadOnlyMutation).toHaveBeenCalledOnce();
    expect(readonly.forward).not.toHaveBeenCalled();

    const admin = handlers();
    await admitAndForwardBackendRequest(request, admin);
    expect(admin.injectApiKey).toHaveBeenCalledOnce();
    expect(admin.forward).toHaveBeenCalledOnce();
  });
  it.each(["/api/recheck-file", "/API/SEARCH-FILE-IN-ARR/", "/%61pi/recheck-file/"])(
    "does not inject a session key for Files mutation %s",
    async (path) => {
      const inject = vi.fn(() => Promise.resolve());
      const callbacks = handlers({
        injectApiKey: () => (isFilesBackendMutation("POST", path) ? Promise.resolve() : inject()),
      });
      await admitAndForwardBackendRequest(
        {
          requiresMetricsAuthentication: false,
          isReadOnlyMutation: isReadOnlyDeniedBackendMutation("POST", path),
          userAgent: undefined,
        },
        callbacks,
      );
      expect(inject).not.toHaveBeenCalled();
      expect(callbacks.forward).toHaveBeenCalledOnce();
    },
  );
  it("does not observe or forward rejected metrics requests", async () => {
    const callbacks = handlers({
      isAuthenticated: vi.fn(() => Promise.resolve(false)),
    });

    await admitAndForwardBackendRequest(
      {
        requiresMetricsAuthentication: true,
        isReadOnlyMutation: false,
        userAgent: "rclone/v1.70.3",
      },
      callbacks,
    );

    expect(callbacks.rejectMetrics).toHaveBeenCalledOnce();
    expect(callbacks.injectApiKey).not.toHaveBeenCalled();
    expect(callbacks.observeRclone).not.toHaveBeenCalled();
    expect(callbacks.forward).not.toHaveBeenCalled();
  });

  it("does not observe or forward rejected read-only mutations", async () => {
    const callbacks = handlers({
      getRole: vi.fn(() => Promise.resolve("readonly")),
    });

    await admitAndForwardBackendRequest(
      {
        requiresMetricsAuthentication: false,
        isReadOnlyMutation: true,
        userAgent: "rclone/v1.70.3",
      },
      callbacks,
    );

    expect(callbacks.injectApiKey).toHaveBeenCalledOnce();
    expect(callbacks.rejectReadOnlyMutation).toHaveBeenCalledOnce();
    expect(callbacks.observeRclone).not.toHaveBeenCalled();
    expect(callbacks.forward).not.toHaveBeenCalled();
  });

  it("observes immediately before forwarding admitted requests", async () => {
    const calls: string[] = [];
    const callbacks = handlers({
      observeRclone: vi.fn(() => calls.push("observe")),
      forward: vi.fn(() => calls.push("forward")),
    });

    await admitAndForwardBackendRequest(
      {
        requiresMetricsAuthentication: false,
        isReadOnlyMutation: false,
        userAgent: "rclone/v1.70.3",
      },
      callbacks,
    );

    expect(calls).toEqual(["observe", "forward"]);
  });
});
