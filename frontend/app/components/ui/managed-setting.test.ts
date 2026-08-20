import { describe, expect, it } from "vitest";
import { omitManagedConfigKeys, pinManagedConfigKeys, type ManagedEnvMap } from "./managed-setting";

describe("omitManagedConfigKeys", () => {
  it("returns the payload unchanged when nothing is managed", () => {
    const changed = { "api.categories": "tv", "webdav.user": "admin" };
    expect(omitManagedConfigKeys(changed, {})).toEqual(changed);
  });

  it("drops only ENV-managed keys from the save payload", () => {
    const managed: ManagedEnvMap = {
      "api.categories": "NZBDAV_CONFIG__API__CATEGORIES",
    };
    expect(
      omitManagedConfigKeys(
        {
          "api.categories": "movies",
          "webdav.user": "admin",
          "webdav.show-hidden-files": "true",
        },
        managed,
      ),
    ).toEqual({
      "webdav.user": "admin",
      "webdav.show-hidden-files": "true",
    });
  });
});

describe("pinManagedConfigKeys", () => {
  it("returns next unchanged when nothing is managed", () => {
    const next = { "api.categories": "movies" };
    expect(pinManagedConfigKeys(next, { "api.categories": "tv" }, {})).toEqual(next);
  });

  it("re-pins managed keys to the loaded baseline", () => {
    const managed: ManagedEnvMap = {
      "api.categories": "NZBDAV_CONFIG__API__CATEGORIES",
      "webdav.user": "NZBDAV_CONFIG__WEBDAV__USER",
    };
    const baseline = {
      "api.categories": "env-cats",
      "webdav.user": "env-user",
      "webdav.show-hidden-files": "false",
    };
    const next = {
      "api.categories": "mutated",
      "webdav.user": "also-mutated",
      "webdav.show-hidden-files": "true",
    };

    expect(pinManagedConfigKeys(next, baseline, managed)).toEqual({
      "api.categories": "env-cats",
      "webdav.user": "env-user",
      "webdav.show-hidden-files": "true",
    });
  });
});
