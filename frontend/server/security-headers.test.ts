import { describe, expect, it, vi } from "vitest";
import { securityHeadersMiddleware } from "./security-headers";

function mockRes() {
  const headers = new Map<string, string>();
  return {
    headers,
    setHeader: (name: string, value: string) => {
      headers.set(name, value);
    },
  };
}

describe("securityHeadersMiddleware", () => {
  it("sets defensive headers on UI paths", () => {
    const res = mockRes();
    const next = vi.fn();
    securityHeadersMiddleware({ method: "GET", path: "/queue" } as never, res as never, next);

    expect(res.headers.get("X-Content-Type-Options")).toBe("nosniff");
    expect(res.headers.get("Referrer-Policy")).toBe("same-origin");
    expect(res.headers.get("X-Frame-Options")).toBe("SAMEORIGIN");
    expect(next).toHaveBeenCalledOnce();
  });

  it("skips headers on proxied backend paths", () => {
    const res = mockRes();
    const next = vi.fn();
    securityHeadersMiddleware(
      { method: "GET", path: "/view/foo.mkv" } as never,
      res as never,
      next,
    );

    expect(res.headers.size).toBe(0);
    expect(next).toHaveBeenCalledOnce();
  });

  it("skips headers on WebDAV methods", () => {
    const res = mockRes();
    const next = vi.fn();
    securityHeadersMiddleware({ method: "PROPFIND", path: "/" } as never, res as never, next);

    expect(res.headers.size).toBe(0);
    expect(next).toHaveBeenCalledOnce();
  });
});
