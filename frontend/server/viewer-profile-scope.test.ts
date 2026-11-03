import { describe, expect, it } from "vitest";
import type express from "express";
import { applyViewerProfileScope, isViewerProfileAllowedBackendPath } from "./viewer-profile-scope";

function request(headers: Record<string, string> = {}): express.Request {
  return { headers: { ...headers } } as unknown as express.Request;
}

describe("viewer profile scope", () => {
  it.each(["/api/catalog/history", "/api/catalog/playback", "/api/search/x/play/y.mkv"])(
    "allows %s for a profile session",
    (pathname) => {
      expect(isViewerProfileAllowedBackendPath(pathname)).toBe(true);
    },
  );

  it.each(["/api/update-config", "/api/viewer-profiles", "/api/get-config", "/view/x"])(
    "denies %s for a profile session",
    (pathname) => {
      expect(isViewerProfileAllowedBackendPath(pathname)).toBe(false);
    },
  );

  it("drops a spoofed header when the session has no profile", () => {
    const req = request({ "x-viewer-profile-id": "9" });

    applyViewerProfileScope(req, null);

    expect(req.headers["x-viewer-profile-id"]).toBeUndefined();
  });

  it("overwrites a spoofed header with the session profile", () => {
    const req = request({ "x-viewer-profile-id": "9" });

    applyViewerProfileScope(req, 4);

    expect(req.headers["x-viewer-profile-id"]).toBe("4");
  });
});
