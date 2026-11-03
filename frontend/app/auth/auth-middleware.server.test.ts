import { beforeEach, describe, expect, it, vi } from "vitest";
import type express from "express";
import { authMiddleware } from "./auth-middleware.server";

const { getSessionUserMock, isAuthenticatedMock } = vi.hoisted(() => ({
  getSessionUserMock: vi.fn(),
  isAuthenticatedMock: vi.fn(),
}));

vi.mock("~/auth/authentication.server", () => ({
  getSessionUser: getSessionUserMock,
  isAuthenticated: isAuthenticatedMock,
}));

function mockReq(path: string): express.Request {
  return { path } as express.Request;
}

function mockRes() {
  return {
    redirect: vi.fn(),
  } as unknown as Omit<express.Response, "redirect"> & { redirect: ReturnType<typeof vi.fn> };
}

beforeEach(() => {
  getSessionUserMock.mockReset();
  getSessionUserMock.mockResolvedValue(null);
  isAuthenticatedMock.mockReset();
});

describe("authMiddleware", () => {
  it.each([
    "/login",
    "/login.data",
    "/login/admin",
    "/login/admin.data",
    "/onboarding",
    "/onboarding.data",
    "/auth/oidc/login",
    "/auth/oidc/callback",
    "/__manifest",
  ])("allows public path %s without auth", async (path) => {
    const next = vi.fn();
    const res = mockRes();

    await authMiddleware(mockReq(path), res as unknown as express.Response, next);

    expect(next).toHaveBeenCalledOnce();
    expect(res.redirect).not.toHaveBeenCalled();
    expect(isAuthenticatedMock).not.toHaveBeenCalled();
  });

  it("allows authenticated sessions on protected paths", async () => {
    isAuthenticatedMock.mockResolvedValueOnce(true);
    const next = vi.fn();
    const res = mockRes();

    await authMiddleware(mockReq("/settings"), res as unknown as express.Response, next);

    expect(isAuthenticatedMock).toHaveBeenCalledOnce();
    expect(next).toHaveBeenCalledOnce();
    expect(res.redirect).not.toHaveBeenCalled();
  });

  it("redirects unauthenticated requests to /login", async () => {
    isAuthenticatedMock.mockResolvedValueOnce(false);
    const next = vi.fn();
    const res = mockRes();

    await authMiddleware(mockReq("/queue"), res as unknown as express.Response, next);

    expect(res.redirect).toHaveBeenCalledWith(302, "/login");
    expect(next).not.toHaveBeenCalled();
  });

  it("sends a profile session away from admin surfaces", async () => {
    getSessionUserMock.mockResolvedValue({ username: "Kids", role: "profile" });
    isAuthenticatedMock.mockResolvedValueOnce(true);
    const next = vi.fn();
    const res = mockRes();

    await authMiddleware(mockReq("/overview"), res as unknown as express.Response, next);

    expect(res.redirect).toHaveBeenCalledWith(302, "/watch");
    expect(next).not.toHaveBeenCalled();
  });

  it.each(["/watch", "/watch/title/movie/10331", "/logout.data", "/login/admin"])(
    "lets a profile session through to %s",
    async (path) => {
      getSessionUserMock.mockResolvedValue({ username: "Kids", role: "profile" });
      isAuthenticatedMock.mockResolvedValueOnce(true);
      const next = vi.fn();
      const res = mockRes();

      await authMiddleware(mockReq(path), res as unknown as express.Response, next);

      expect(next).toHaveBeenCalledOnce();
      expect(res.redirect).not.toHaveBeenCalled();
    },
  );

  it("lets an admin session reach admin surfaces", async () => {
    getSessionUserMock.mockResolvedValue({ username: "admin", role: "admin" });
    isAuthenticatedMock.mockResolvedValueOnce(true);
    const next = vi.fn();
    const res = mockRes();

    await authMiddleware(mockReq("/overview"), res as unknown as express.Response, next);

    expect(next).toHaveBeenCalledOnce();
    expect(res.redirect).not.toHaveBeenCalled();
  });

  it("decodes URI-encoded public paths", async () => {
    const next = vi.fn();
    const res = mockRes();

    await authMiddleware(mockReq("/%6Fnboarding"), res as unknown as express.Response, next);

    expect(next).toHaveBeenCalledOnce();
    expect(isAuthenticatedMock).not.toHaveBeenCalled();
  });

  it("redirects malformed URI paths instead of throwing", async () => {
    isAuthenticatedMock.mockResolvedValueOnce(false);
    const next = vi.fn();
    const res = mockRes();

    await authMiddleware(mockReq("/%E0%A4%A"), res as unknown as express.Response, next);

    expect(res.redirect).toHaveBeenCalledWith(302, "/login");
    expect(next).not.toHaveBeenCalled();
  });
});
