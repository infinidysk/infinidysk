import { describe, expect, it } from "vitest";
import {
  isWatchtowerListSourceMaxResponseBytesValid,
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
