import type { IncomingMessage, ServerResponse } from "node:http";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { BACKEND_FAILURE_LOG_THROTTLE_MS } from "./startup-grace";

type ProxyErrorHandler = (
  error: unknown,
  req: IncomingMessage,
  res: ServerResponse,
) => void;

const harness = vi.hoisted(() => ({
  onError: undefined as ProxyErrorHandler | undefined,
  startupGrace: false,
  logger: { debug: vi.fn(), info: vi.fn(), warn: vi.fn(), error: vi.fn() },
}));

vi.mock("http-proxy-middleware", () => ({
  createProxyMiddleware: (options: { on: { error: ProxyErrorHandler } }) => {
    harness.onError = options.on.error;
    return vi.fn();
  },
}));
vi.mock("./logger", async (importOriginal) => ({
  ...(await importOriginal<typeof import("./logger")>()),
  logger: harness.logger,
}));
vi.mock("./startup-grace", async (importOriginal) => ({
  ...(await importOriginal<typeof import("./startup-grace")>()),
  isWithinBackendStartupGrace: () => harness.startupGrace,
}));
vi.mock("./react-router-request-handler", () => ({ createRequestHandler: () => vi.fn() }));
vi.mock("./websocket.server", () => ({ websocketServer: { initialize: vi.fn() } }));
vi.mock("./runtime-config", () => ({
  getFrontendRuntimeConfig: vi.fn(),
  installFrontendRuntimeConfig: vi.fn(),
}));
vi.mock("~/auth/auth-middleware.server", () => ({ authMiddleware: vi.fn() }));
vi.mock("~/auth/authentication.server", () => ({
  getSessionUser: vi.fn(),
  isAuthenticated: vi.fn(),
}));
vi.mock("./inject-api-key.server", () => ({ setApiKeyForAuthenticatedRequests: vi.fn() }));
vi.mock("./configured-action-origin", () => ({
  isTrustProxyEnabled: () => false,
  refreshProxySettings: vi.fn(),
  resolveConfiguredActionOrigin: vi.fn(),
}));
vi.mock("./oidc-routes", () => ({ oidcRouter: vi.fn() }));
vi.mock("~/utils/url-base", () => ({ URL_BASE: "" }));

const apiKey = "synthetic-1441-api-key";
const nestedToken = "synthetic-1441-indexer-token";
const requestUrl =
  "/api?mode=addurl&apikey=synthetic-1441-api-key&name=" +
  encodeURIComponent(`https://indexer.example.test/nzb?token=${nestedToken}`);

function invokeProxyError(error: unknown, headersSent = false) {
  const req = { method: "GET", url: requestUrl } as IncomingMessage;
  const res = { headersSent, writeHead: vi.fn(), end: vi.fn() };
  expect(harness.onError).toBeTypeOf("function");
  harness.onError!(error, req, res as unknown as ServerResponse);
  return { req, res };
}

function expectNoQuerySecrets(): void {
  const text = Object.values(harness.logger)
    .flatMap((log) => log.mock.calls)
    .flat()
    .map((value: unknown) =>
      value instanceof Error ? (value.stack ?? value.message) : String(value),
    )
    .join("\n");
  expect(text).not.toContain(apiKey);
  expect(text).not.toContain(nestedToken);
  expect(text).not.toContain("?mode=");
}

beforeEach(async () => {
  vi.resetModules();
  vi.clearAllMocks();
  harness.onError = undefined;
  harness.startupGrace = false;
  vi.stubEnv("BACKEND_URL", "http://127.0.0.1:1");
  vi.spyOn(Date, "now").mockReturnValue(1_000_000);
  await import("./app");
});

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllEnvs();
});

describe("backend proxy error logging", () => {
  it("omits query credentials and expected error stacks while returning 502", () => {
    const error = Object.assign(new Error(`connect failed for ${requestUrl}`), {
      code: "ECONNREFUSED",
    });
    const { req, res } = invokeProxyError(error);

    expect(harness.logger.warn).toHaveBeenCalledTimes(1);
    expect(harness.logger.warn).toHaveBeenCalledWith(
      "Backend proxy failed for GET /api. Reason: ECONNREFUSED",
    );
    expect(harness.logger.info).not.toHaveBeenCalled();
    expect(harness.logger.error).not.toHaveBeenCalled();
    expect(harness.logger.debug).not.toHaveBeenCalled();
    expect(res.writeHead).toHaveBeenCalledTimes(1);
    expect(res.writeHead).toHaveBeenCalledWith(502, { "Content-Type": "text/plain" });
    expect(res.end).toHaveBeenCalledTimes(1);
    expect(res.end).toHaveBeenCalledWith("Bad Gateway");
    expect(req.url).toBe(requestUrl);
    expectNoQuerySecrets();
  });

  it("preserves the proxy-failure throttle", () => {
    invokeProxyError(Object.assign(new Error("refused"), { code: "ECONNREFUSED" }));
    invokeProxyError(Object.assign(new Error("refused"), { code: "ECONNREFUSED" }));
    expect(harness.logger.warn).toHaveBeenCalledTimes(1);

    vi.mocked(Date.now).mockReturnValue(1_000_000 + BACKEND_FAILURE_LOG_THROTTLE_MS);
    const { res } = invokeProxyError(Object.assign(new Error("refused"), { code: "ECONNREFUSED" }));

    expect(harness.logger.warn).toHaveBeenCalledTimes(2);
    expect(res.writeHead).toHaveBeenCalledWith(502, { "Content-Type": "text/plain" });
    expect(res.end).toHaveBeenCalledWith("Bad Gateway");
    expectNoQuerySecrets();
  });

  it("keeps startup failures at one query-free wait message", () => {
    harness.startupGrace = true;
    const first = invokeProxyError(Object.assign(new Error("refused"), { code: "ECONNREFUSED" }));
    const second = invokeProxyError(Object.assign(new Error("refused"), { code: "ECONNREFUSED" }));

    expect(harness.logger.info).toHaveBeenCalledTimes(1);
    expect(harness.logger.info).toHaveBeenCalledWith("Waiting for backend to start...");
    expect(harness.logger.warn).not.toHaveBeenCalled();
    expect(harness.logger.error).not.toHaveBeenCalled();
    for (const { res } of [first, second]) {
      expect(res.writeHead).toHaveBeenCalledWith(502, { "Content-Type": "text/plain" });
      expect(res.end).toHaveBeenCalledWith("Bad Gateway");
    }
    expectNoQuerySecrets();

    harness.startupGrace = false;
    vi.mocked(Date.now).mockReturnValue(1_000_000 + BACKEND_FAILURE_LOG_THROTTLE_MS);
    invokeProxyError(Object.assign(new Error("refused"), { code: "ECONNREFUSED" }));
    expect(harness.logger.warn).toHaveBeenCalledTimes(1);
    expectNoQuerySecrets();
  });

  it("uses a safe reason for wrapped expected errors", () => {
    const error = new Error("synthetic wrapper", {
      cause: Object.assign(new Error(requestUrl), { code: "ECONNRESET" }),
    });
    const { res } = invokeProxyError(error);

    expect(harness.logger.warn).toHaveBeenCalledTimes(1);
    expect(harness.logger.warn).toHaveBeenCalledWith(
      "Backend proxy failed for GET /api. Reason: backend connection unavailable",
    );
    expect(res.writeHead).toHaveBeenCalledWith(502, { "Content-Type": "text/plain" });
    expectNoQuerySecrets();
  });

  it("preserves unexpected-error diagnostics with a sanitized request path", () => {
    const error = new Error("synthetic unexpected proxy failure");
    const { res } = invokeProxyError(error);

    expect(harness.logger.warn).toHaveBeenCalledTimes(1);
    expect(harness.logger.warn).toHaveBeenCalledWith("Backend proxy failed for GET /api", error);
    expect(res.writeHead).toHaveBeenCalledWith(502, { "Content-Type": "text/plain" });
    expectNoQuerySecrets();
  });

  it("does not rewrite a response whose headers are already sent", () => {
    const { res } = invokeProxyError(
      Object.assign(new Error(`connect failed for ${requestUrl}`), { code: "ECONNREFUSED" }),
      true,
    );

    expect(harness.logger.warn).toHaveBeenCalledTimes(1);
    expect(harness.logger.warn).toHaveBeenCalledWith(
      "Backend proxy failed for GET /api. Reason: ECONNREFUSED",
    );
    expect(res.writeHead).not.toHaveBeenCalled();
    expect(res.end).not.toHaveBeenCalled();
    expectNoQuerySecrets();
  });
});
