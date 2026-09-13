import { describe, expect, it } from "vitest";
import type express from "express";
import { applyCanonicalForwardedHeaders, normalizeForwardedHost } from "./forwarded-headers";

function fakeRequest(headers: Record<string, string | string[]>): express.Request {
  return { headers, protocol: "http" } as unknown as express.Request;
}

describe("normalizeForwardedHost", () => {
  it("rewrites the Host header to the forwarded host, dropping the internal port", () => {
    const req = fakeRequest({
      host: "internal-container:3000",
      "x-forwarded-host": "public.example.com",
      "x-forwarded-proto": "https",
    });

    normalizeForwardedHost(req, true);

    expect(req.headers.host).toBe("public.example.com");
  });

  it("uses the first entry when X-Forwarded-Host has multiple comma-separated hops", () => {
    const req = fakeRequest({
      host: "internal-container:3000",
      "x-forwarded-host": "public.example.com, edge.internal",
      "x-forwarded-proto": "https",
    });

    normalizeForwardedHost(req, true);

    expect(req.headers.host).toBe("public.example.com");
  });

  it("leaves the Host header untouched when trustProxy is disabled", () => {
    const req = fakeRequest({
      host: "internal-container:3000",
      "x-forwarded-host": "public.example.com",
      "x-forwarded-proto": "https",
    });

    normalizeForwardedHost(req, false);

    expect(req.headers.host).toBe("internal-container:3000");
  });

  it("leaves the Host header untouched when X-Forwarded-Host is absent", () => {
    const req = fakeRequest({ host: "internal-container:3000" });

    normalizeForwardedHost(req, true);

    expect(req.headers.host).toBe("internal-container:3000");
  });

  it("keeps an explicit non-default port carried on X-Forwarded-Host", () => {
    const req = fakeRequest({
      host: "internal-container:3000",
      "x-forwarded-host": "public.example.com:8443",
      "x-forwarded-proto": "https",
    });

    normalizeForwardedHost(req, true);

    expect(req.headers.host).toBe("public.example.com:8443");
  });

  it("appends a non-default X-Forwarded-Port when X-Forwarded-Host omits one", () => {
    const req = fakeRequest({
      host: "internal-container:3000",
      "x-forwarded-host": "public.example.com",
      "x-forwarded-proto": "https",
      "x-forwarded-port": "8443",
    });

    normalizeForwardedHost(req, true);

    expect(req.headers.host).toBe("public.example.com:8443");
  });

  it("omits a default port (443) reported via X-Forwarded-Port for https", () => {
    const req = fakeRequest({
      host: "internal-container:3000",
      "x-forwarded-host": "public.example.com",
      "x-forwarded-proto": "https",
      "x-forwarded-port": "443",
    });

    normalizeForwardedHost(req, true);

    expect(req.headers.host).toBe("public.example.com");
  });

  it("omits a default port (80) reported via X-Forwarded-Port for http", () => {
    const req = fakeRequest({
      host: "internal-container:3000",
      "x-forwarded-host": "public.example.com",
      "x-forwarded-proto": "http",
      "x-forwarded-port": "80",
    });

    normalizeForwardedHost(req, true);

    expect(req.headers.host).toBe("public.example.com");
  });

  it("falls back to req.protocol when X-Forwarded-Proto is absent", () => {
    const req = { headers: { "x-forwarded-host": "public.example.com" }, protocol: "https" } as unknown as express.Request;

    normalizeForwardedHost(req, true);

    expect(req.headers.host).toBe("public.example.com");
  });

  it("handles bracketed IPv6 forwarded hosts without splitting the address", () => {
    const req = fakeRequest({
      host: "internal-container:3000",
      "x-forwarded-host": "[2001:db8::1]:8443",
      "x-forwarded-proto": "https",
    });

    normalizeForwardedHost(req, true);

    expect(req.headers.host).toBe("[2001:db8::1]:8443");
  });
});

describe("applyCanonicalForwardedHeaders", () => {
  it("strips client forwarded headers and sets canonical values from the socket", () => {
    const removed: string[] = [];
    const set: Record<string, string> = {};
    const proxyReq = {
      removeHeader: (name: string) => {
        removed.push(name);
      },
      setHeader: (name: string, value: string) => {
        set[name] = value;
      },
    };

    const req = {
      protocol: "https",
      get: (name: string) => (name.toLowerCase() === "host" ? "nzbdav.example" : undefined),
      ip: "203.0.113.10",
      socket: { remoteAddress: "10.0.0.2" },
    } as unknown as express.Request;

    applyCanonicalForwardedHeaders(proxyReq, req);

    expect(removed).toEqual(
      expect.arrayContaining([
        "x-forwarded-for",
        "x-forwarded-host",
        "x-forwarded-proto",
        "x-forwarded-port",
        "forwarded",
      ]),
    );
    expect(set["X-Forwarded-Proto"]).toBe("https");
    expect(set["X-Forwarded-Host"]).toBe("nzbdav.example");
    expect(set["X-Forwarded-For"]).toBe("10.0.0.2");
  });

  it("uses req.ip when trustProxy is enabled", () => {
    const set: Record<string, string> = {};
    const proxyReq = {
      removeHeader: () => {},
      setHeader: (name: string, value: string) => {
        set[name] = value;
      },
    };

    const req = {
      protocol: "https",
      get: () => "nzbdav.example",
      ip: "203.0.113.10",
      socket: { remoteAddress: "10.0.0.2" },
    } as unknown as express.Request;

    applyCanonicalForwardedHeaders(proxyReq, req, { trustProxy: true });

    expect(set["X-Forwarded-For"]).toBe("203.0.113.10");
  });

  it("forwards the configured URL base for backend-generated links", () => {
    const set: Record<string, string> = {};
    const proxyReq = {
      removeHeader: () => {},
      setHeader: (name: string, value: string) => {
        set[name] = value;
      },
    };
    const req = {
      protocol: "https",
      get: () => "nzbdav.example",
      socket: { remoteAddress: "10.0.0.2" },
    } as unknown as express.Request;

    applyCanonicalForwardedHeaders(proxyReq, req, { pathBase: "/nzbdav" });

    expect(set["X-Forwarded-Prefix"]).toBe("/nzbdav");
  });
});
