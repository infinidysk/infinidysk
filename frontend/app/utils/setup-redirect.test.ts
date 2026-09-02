import { describe, expect, it } from "vitest";
import { buildSetupRedirect } from "./setup-redirect";

describe("buildSetupRedirect", () => {
  it("redirects an admin to setup while preserving the requested page", () => {
    expect(buildSetupRedirect("/queue", "?page=2", true, true)).toBe(
      "/setup?returnTo=%2Fqueue%3Fpage%3D2",
    );
  });

  it("uses Overview as the root return target", () => {
    expect(buildSetupRedirect("/", "", true, true)).toBe("/setup?returnTo=%2Foverview");
  });

  it.each([
    ["/overview", "", false, true],
    ["/overview", "", true, false],
    ["/setup", "", true, true],
  ] as const)(
    "does not redirect %s when configuration is unavailable",
    (path, search, canConfigure, setupRequired) => {
      expect(buildSetupRedirect(path, search, canConfigure, setupRequired)).toBeNull();
    },
  );
});
