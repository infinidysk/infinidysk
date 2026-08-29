import { describe, expect, it } from "vitest";
import {
  isBackendApiDocsPath,
  isBackendApiPath,
  isReadOnlyDeniedBackendMutation,
  matchesBackendPathPrefix,
  safeDecodePath,
  shouldProxyToBackend,
  shouldSkipCompression,
} from "./proxy-path";

describe("safeDecodePath", () => {
  it("decodes valid percent-encoding", () => {
    expect(safeDecodePath("/%61pi/get-config")).toBe("/api/get-config");
  });

  it.each(["/%zz", "/view%", "/%E0%A4%A"])("returns null for malformed path %s", (path) => {
    expect(safeDecodePath(path)).toBeNull();
  });
});

describe("matchesBackendPathPrefix", () => {
  it.each([
    "/api",
    "/api/get-config",
    "/ready",
    "/view",
    "/view/movies",
    "/adapters",
    "/adapters/addon/token/manifest.json",
  ])("matches %s", (path) => {
    expect(matchesBackendPathPrefix(path)).toBe(true);
  });

  it.each(["/apifoo", "/readyfoo", "/viewport.css", "/contents-page", "/adaptersfoo"])(
    "rejects bare-prefix false positive %s",
    (path) => {
      expect(matchesBackendPathPrefix(path)).toBe(false);
    },
  );
});

describe("isBackendApiPath", () => {
  it("matches /api and children", () => {
    expect(isBackendApiPath("/api")).toBe(true);
    expect(isBackendApiPath("/api/get-config")).toBe(true);
    expect(isBackendApiPath("/%61pi/get-config")).toBe(true);
  });

  it("rejects /apifoo and non-api paths", () => {
    expect(isBackendApiPath("/apifoo")).toBe(false);
    expect(isBackendApiPath("/view")).toBe(false);
  });
});

describe("isReadOnlyDeniedBackendMutation", () => {
  it.each([
    "/api/delete-webdav-item",
    "/api/delete-webdav-item/preview",
    "/api/remove-missing-payloads",
    "/api/remove-missing-payloads/",
    "/api/remove-missing-payloads%2F",
    "/%61pi/remove-missing-payloads",
    "/api/trigger-health-check",
    "/api/trigger-health-check/run",
    "/api/trigger-health-check%2Frun",
  ])("blocks read-only POST access to %s", (path) => {
    expect(isReadOnlyDeniedBackendMutation("POST", path)).toBe(true);
  });

  it.each([
    ["GET", "/api/remove-missing-payloads"],
    ["GET", "/api/trigger-health-check"],
    ["POST", "/api/remove-missing-payloads/dry-run"],
    ["POST", "/api/remove-missing-payloads/audit"],
    ["POST", "/api/remove-unlinked-files"],
  ])("allows %s %s", (method, path) => {
    expect(isReadOnlyDeniedBackendMutation(method, path)).toBe(false);
  });
});

describe("isBackendApiDocsPath", () => {
  it.each(["/openapi", "/openapi/admin.json", "/scalar", "/scalar/", "/scalar/admin"])(
    "matches API docs path %s",
    (path) => {
      expect(isBackendApiDocsPath(path)).toBe(true);
    },
  );

  it.each(["/openapifoo", "/scalarfoo", "/api/get-config", "/%zz"])(
    "rejects non-docs path %s",
    (path) => {
      expect(isBackendApiDocsPath(path)).toBe(false);
    },
  );
});

describe("shouldProxyToBackend", () => {
  it.each(["PROPFIND", "propfind", "OPTIONS", "options"])(
    "proxies %s requests regardless of path",
    (method) => {
      expect(shouldProxyToBackend(method, "/unrelated")).toBe(true);
      expect(shouldProxyToBackend(method, "/apifoo")).toBe(true);
    },
  );

  it.each([
    "/api",
    "/api/get-config",
    "/ready",
    "/view",
    "/view/movies",
    "/.ids/item",
    "/nzbs/file.nzb",
    "/content/file.mkv",
    "/completed-symlinks/movie",
    "/adapters/addon/profile-token/manifest.json",
    "/adapters/newznab/profile-token/api",
    "/README",
  ])("proxies backend path %s", (path) => {
    expect(shouldProxyToBackend("GET", path)).toBe(true);
  });

  it("checks decoded paths", () => {
    expect(shouldProxyToBackend("GET", "/%61pi/get-config")).toBe(true);
  });

  it.each(["/%zz", "/view%"])("does not proxy malformed path %s", (path) => {
    expect(shouldProxyToBackend("GET", path)).toBe(false);
  });

  it.each(["/apifoo", "/viewport.css", "/contents-page", "/READMEfoo"])(
    "does not proxy bare-prefix false positive %s",
    (path) => {
      expect(shouldProxyToBackend("GET", path)).toBe(false);
    },
  );

  it.each(["/", "/login", "/settings", "/assets/app.js"])(
    "leaves frontend path %s to React Router",
    (path) => {
      expect(shouldProxyToBackend("GET", path)).toBe(false);
    },
  );
});

describe("shouldSkipCompression", () => {
  it("skips compression for backend paths", () => {
    expect(shouldSkipCompression("/view/movies")).toBe(true);
    expect(shouldSkipCompression("/api/get-config")).toBe(true);
    expect(shouldSkipCompression("/openapi/admin.json")).toBe(true);
    expect(shouldSkipCompression("/scalar/")).toBe(true);
  });

  it("does not skip for frontend paths, false positives, or malformed encoding", () => {
    expect(shouldSkipCompression("/login")).toBe(false);
    expect(shouldSkipCompression("/viewport.css")).toBe(false);
    expect(shouldSkipCompression("/%zz")).toBe(false);
  });

  it("does not double-decode API documentation paths", () => {
    expect(shouldSkipCompression("/scalar/%25")).toBe(true);
  });
});
