import { EventEmitter } from "node:events";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { logger } from "./logger";
import { resetClientErrorLogThrottleForTests } from "./request-log-throttle";
import {
  attachBrowserWebsocketErrorListener,
  MAX_WEBSOCKET_PAYLOAD_BYTES,
  reportBrowserSocketError,
} from "./websocket-policy";

const PAYLOAD_MARKER = "FAKE_PAYLOAD_MARKER_xyzzy";

function codedError(code: string, message: string): Error {
  return Object.assign(new Error(message), { code });
}

describe("browser websocket error policy", () => {
  beforeEach(() => {
    resetClientErrorLogThrottleForTests();
  });

  afterEach(() => {
    resetClientErrorLogThrottleForTests();
    vi.restoreAllMocks();
  });

  it("uses a 64 KiB payload limit", () => {
    expect(MAX_WEBSOCKET_PAYLOAD_BYTES).toBe(64 * 1024);
  });

  it("warns for oversized frames without the Error object or payload marker", () => {
    const warn = vi.spyOn(logger, "warn").mockImplementation(() => {});
    const errorLog = vi.spyOn(logger, "error").mockImplementation(() => {});
    const error = codedError("WS_ERR_UNSUPPORTED_MESSAGE_LENGTH", PAYLOAD_MARKER);

    reportBrowserSocketError(error, {
      remote: "127.0.0.1",
      isAuthenticated: () => false,
    });

    expect(warn).toHaveBeenCalledOnce();
    expect(warn.mock.calls[0]?.[1]).toBeUndefined();
    const message = String(warn.mock.calls[0]?.[0]);
    expect(message).toContain("Browser websocket rejected oversized frame");
    expect(message).toContain("127.0.0.1");
    expect(message).toContain("pre-auth");
    expect(message).toContain(String(MAX_WEBSOCKET_PAYLOAD_BYTES));
    expect(message).not.toContain(PAYLOAD_MARKER);
    expect(errorLog).not.toHaveBeenCalled();
  });

  it("warns for expected transport errors without the Error object", () => {
    const warn = vi.spyOn(logger, "warn").mockImplementation(() => {});
    const errorLog = vi.spyOn(logger, "error").mockImplementation(() => {});
    const error = codedError("ECONNRESET", PAYLOAD_MARKER);

    reportBrowserSocketError(error, {
      remote: "127.0.0.1",
      isAuthenticated: () => true,
    });

    expect(warn).toHaveBeenCalledOnce();
    expect(warn.mock.calls[0]?.[1]).toBeUndefined();
    const message = String(warn.mock.calls[0]?.[0]);
    expect(message).toContain("Browser websocket peer error");
    expect(message).toContain("127.0.0.1");
    expect(message).toContain("authenticated");
    expect(message).toContain("ECONNRESET");
    expect(message).not.toContain(PAYLOAD_MARKER);
    expect(errorLog).not.toHaveBeenCalled();
  });

  it("warns for other WS_ERR_* codes with the bounded code only", () => {
    const warn = vi.spyOn(logger, "warn").mockImplementation(() => {});
    reportBrowserSocketError(codedError("WS_ERR_INVALID_UTF8", PAYLOAD_MARKER), {
      remote: "10.0.0.2",
      isAuthenticated: () => false,
    });

    const message = String(warn.mock.calls[0]?.[0]);
    expect(message).toContain("WS_ERR_INVALID_UTF8");
    expect(message).not.toContain(PAYLOAD_MARKER);
  });

  it("logs unexpected errors with the original Error object", () => {
    const errorLog = vi.spyOn(logger, "error").mockImplementation(() => {});
    const error = codedError("EPROTO", "unexpected-socket-failure");

    reportBrowserSocketError(error, {
      remote: "127.0.0.1",
      isAuthenticated: () => false,
    });

    expect(errorLog).toHaveBeenCalledOnce();
    expect(errorLog).toHaveBeenCalledWith(
      "Unexpected browser websocket error from 127.0.0.1 during pre-auth",
      error,
    );
  });

  it("throttles repeated expected errors under a coarse key that omits the remote address", () => {
    const warn = vi.spyOn(logger, "warn").mockImplementation(() => {});
    const first = codedError("WS_ERR_UNSUPPORTED_MESSAGE_LENGTH", PAYLOAD_MARKER);
    const second = codedError("WS_ERR_UNSUPPORTED_MESSAGE_LENGTH", PAYLOAD_MARKER);

    reportBrowserSocketError(first, {
      remote: "127.0.0.1",
      isAuthenticated: () => false,
    });
    reportBrowserSocketError(second, {
      remote: "10.0.0.9",
      isAuthenticated: () => false,
    });

    expect(warn).toHaveBeenCalledOnce();
  });

  it("attaches an error listener that does not close or terminate the socket", () => {
    const warn = vi.spyOn(logger, "warn").mockImplementation(() => {});
    const close = vi.fn();
    const terminate = vi.fn();
    const socket = Object.assign(new EventEmitter(), { close, terminate });

    attachBrowserWebsocketErrorListener(socket, {
      remote: "127.0.0.1",
      isAuthenticated: () => false,
    });

    expect(socket.listenerCount("error")).toBe(1);
    socket.emit("error", codedError("WS_ERR_UNSUPPORTED_MESSAGE_LENGTH", PAYLOAD_MARKER));

    expect(warn).toHaveBeenCalledOnce();
    expect(close).not.toHaveBeenCalled();
    expect(terminate).not.toHaveBeenCalled();
  });
});
