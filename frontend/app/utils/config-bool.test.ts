import { describe, expect, it } from "vitest";
import { parseConfigBoolean } from "./config-bool";

describe("parseConfigBoolean", () => {
  it.each([
    [undefined, true],
    [null, true],
    ["", true],
    ["  ", true],
    ["true", true],
    ["TRUE", true],
    [" true ", true],
    ["false", false],
    ["FALSE", false],
    [" false ", false],
    ["bogus", true],
    ["1", true],
    ["0", true],
  ])("parses %j as %s with default-on fallback", (value, expected) => {
    expect(parseConfigBoolean(value)).toBe(expected);
  });

  it("uses an explicit fallback for invalid text", () => {
    expect(parseConfigBoolean("bogus", false)).toBe(false);
  });
});
