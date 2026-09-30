import { describe, expect, it } from "vitest";
import {
  isWatchtowerListSourceMaxResponseBytesValid,
  isWatchtowerSettingsUpdated,
  isWatchtowerSettingsValid,
} from "./watchtower";

describe("isWatchtowerListSourceMaxResponseBytesValid", () => {
  it("accepts the default and the hard clamp", () => {
    expect(isWatchtowerListSourceMaxResponseBytesValid("8388608")).toBe(true);
    expect(isWatchtowerListSourceMaxResponseBytesValid("1")).toBe(true);
    expect(isWatchtowerListSourceMaxResponseBytesValid("16777216")).toBe(true);
  });

  it("rejects zero, decimals, and values above the hard clamp", () => {
    expect(isWatchtowerListSourceMaxResponseBytesValid("0")).toBe(false);
    expect(isWatchtowerListSourceMaxResponseBytesValid("1.5")).toBe(false);
    expect(isWatchtowerListSourceMaxResponseBytesValid("16777217")).toBe(false);
    expect(isWatchtowerListSourceMaxResponseBytesValid("")).toBe(false);
    expect(isWatchtowerListSourceMaxResponseBytesValid(" 8388608")).toBe(false);
  });
});

describe("isWatchtowerSettingsValid", () => {
  it("gates save on the list-source response-size field", () => {
    expect(
      isWatchtowerSettingsValid({ "watchtower.list-source-max-response-bytes": "8388608" }),
    ).toBe(true);
    expect(isWatchtowerSettingsValid({ "watchtower.list-source-max-response-bytes": "0" })).toBe(
      false,
    );
  });
});

describe("watchtower.schedule", () => {
  const base = { "watchtower.list-source-max-response-bytes": "8388608" };

  it("allows an empty (unrestricted) schedule", () => {
    expect(isWatchtowerSettingsValid({ ...base, "watchtower.schedule": "" })).toBe(true);
  });

  it("accepts a valid window and blocks save on a malformed schedule", () => {
    expect(
      isWatchtowerSettingsValid({
        ...base,
        "watchtower.schedule":
          '{"Enabled":true,"Windows":[{"Days":[0,1,2,3,4,5,6],"StartMinute":120,"EndMinute":540}]}',
      }),
    ).toBe(true);
    expect(isWatchtowerSettingsValid({ ...base, "watchtower.schedule": "{not-json" })).toBe(false);
    expect(
      isWatchtowerSettingsValid({
        ...base,
        "watchtower.schedule": '{"Enabled":true,"Windows":[]}',
      }),
    ).toBe(false);
  });

  it("counts a schedule change as an update", () => {
    expect(
      isWatchtowerSettingsUpdated(
        { "watchtower.schedule": "" },
        { "watchtower.schedule": '{"Enabled":false,"Windows":[]}' },
      ),
    ).toBe(true);
  });
});
