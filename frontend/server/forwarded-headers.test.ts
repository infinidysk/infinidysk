import { describe, expect, it } from "vitest";
import type express from "express";
import { applyCanonicalForwardedHeaders } from "./forwarded-headers";

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
