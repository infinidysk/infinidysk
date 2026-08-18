import { beforeEach, describe, expect, it, vi } from "vitest";
import type express from "express";
import { setApiKeyForAuthenticatedRequests } from "./inject-api-key.server";

const { isAuthenticatedMock } = vi.hoisted(() => ({
  isAuthenticatedMock: vi.fn(),
}));

vi.mock("~/auth/authentication.server", () => ({
  isAuthenticated: isAuthenticatedMock,
}));

beforeEach(() => {
  isAuthenticatedMock.mockReset();
  vi.stubEnv("FRONTEND_BACKEND_API_KEY", "injected-key");
});

function mockReq(partial: {
  path: string;
  query?: Record<string, unknown>;
  headers?: Record<string, string | undefined>;
}): express.Request {
  return {
    path: partial.path,
    query: partial.query ?? {},
    headers: { ...(partial.headers ?? {}) },
  } as express.Request;
}

describe("setApiKeyForAuthenticatedRequests", () => {
  it("ignores non-/api paths", async () => {
    const req = mockReq({ path: "/view/movie" });
    await setApiKeyForAuthenticatedRequests(req);
    expect(isAuthenticatedMock).not.toHaveBeenCalled();
    expect(req.headers["x-api-key"]).toBeUndefined();
  });

  it("ignores /apifoo bare-prefix false positives", async () => {
    const req = mockReq({ path: "/apifoo" });
    await setApiKeyForAuthenticatedRequests(req);
    expect(isAuthenticatedMock).not.toHaveBeenCalled();
  });

  it("injects for decoded /api paths", async () => {
    isAuthenticatedMock.mockResolvedValueOnce(true);
    const req = mockReq({ path: "/%61pi/get-config" });
    await setApiKeyForAuthenticatedRequests(req);
    expect(req.headers["x-api-key"]).toBe("injected-key");
  });

  it("leaves an existing API key alone", async () => {
    const req = mockReq({
      path: "/api/get-config",
      headers: { "x-api-key": "client-key" },
    });
    await setApiKeyForAuthenticatedRequests(req);
    expect(isAuthenticatedMock).not.toHaveBeenCalled();
    expect(req.headers["x-api-key"]).toBe("client-key");
  });

  it("does not inject when the session is unauthenticated", async () => {
    isAuthenticatedMock.mockResolvedValueOnce(false);
    const req = mockReq({ path: "/api/get-config" });
    await setApiKeyForAuthenticatedRequests(req);
    expect(req.headers["x-api-key"]).toBeUndefined();
  });

  it("injects FRONTEND_BACKEND_API_KEY for authenticated /api requests", async () => {
    isAuthenticatedMock.mockResolvedValueOnce(true);
    const req = mockReq({ path: "/api/get-config" });
    await setApiKeyForAuthenticatedRequests(req);
    expect(req.headers["x-api-key"]).toBe("injected-key");
  });

  it("injects FRONTEND_BACKEND_API_KEY for authenticated API docs requests", async () => {
    isAuthenticatedMock.mockResolvedValueOnce(true);
    const req = mockReq({ path: "/openapi/admin.json" });
    await setApiKeyForAuthenticatedRequests(req);
    expect(req.headers["x-api-key"]).toBe("injected-key");
  });

  it("treats query apikey as already present", async () => {
    const req = mockReq({
      path: "/api",
      query: { apikey: "query-key" },
    });
    await setApiKeyForAuthenticatedRequests(req);
    expect(isAuthenticatedMock).not.toHaveBeenCalled();
  });
});
