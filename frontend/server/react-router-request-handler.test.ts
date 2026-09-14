import express from "express";
import { describe, expect, it } from "vitest";
import { resolvePort, resolveRequestUrl } from "./react-router-request-handler";

function fakeRequest(options: {
  trustProxy: boolean;
  host: string;
  forwardedHost?: string;
}): express.Request {
  const app = express();
  if (options.trustProxy) app.set("trust proxy", 1);
  const headers: Record<string, string> = { host: options.host };
  if (options.forwardedHost) headers["x-forwarded-host"] = options.forwardedHost;
  return {
    app,
    get: (name: string) => headers[name.toLowerCase()],
  } as unknown as express.Request;
}

describe("resolvePort", () => {
  it("prefers the forwarded host's port over the raw internal Host port", () => {
    const req = fakeRequest({
      trustProxy: true,
      host: "internal-container:3000",
      forwardedHost: "nzbdav.example.com:8443",
    });

    expect(resolvePort(req)).toBe("8443");
  });

  it("falls back to the raw Host header port when X-Forwarded-Host omits one", () => {
    const req = fakeRequest({ trustProxy: false, host: "internal-container:3000" });

    expect(resolvePort(req)).toBe("3000");
  });

  it("does not misparse a bracketed IPv6 forwarded host as the port (regression)", () => {
    const req = fakeRequest({
      trustProxy: true,
      host: "internal-container:3000",
      forwardedHost: "[2001:db8::1]:8443",
    });

    // Naively splitting "[2001:db8::1]:8443" on ":" yields "db8" as the second
    // segment; @react-router/express's original createRemixRequest treated that
    // as the port, failed to parse it as a number, and silently dropped 8443.
    expect(resolvePort(req)).toBe("8443");
  });

  it("returns undefined when neither host carries an explicit port", () => {
    const req = fakeRequest({ trustProxy: true, host: "internal-container" });

    expect(resolvePort(req)).toBeUndefined();
  });

  it("parses only the first hop of a multi-hop X-Forwarded-Host (regression)", () => {
    const req = fakeRequest({
      trustProxy: true,
      host: "internal-container:3000",
      forwardedHost: "nzbdav.example.com:8443, edge.internal",
    });

    // Without trimming to the first hop, splitHostPort would return
    // "8443, edge.internal" as the port, producing an invalid SSR request URL.
    expect(resolvePort(req)).toBe("8443");
  });
});

describe("resolveRequestUrl", () => {
  it("uses a configured canonical origin instead of the internal container origin", () => {
    const req = {
      ...fakeRequest({ trustProxy: false, host: "internal-container:3000" }),
      hostname: "internal-container",
      originalUrl: "/login.data",
      protocol: "http",
    } as express.Request;

    expect(resolveRequestUrl(req, "https://nzbdav.example.com").href).toBe(
      "https://nzbdav.example.com/login.data",
    );
  });
});
