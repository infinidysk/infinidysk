import { describe, expect, it } from "vitest";
import { getDownloadKey } from "./downloads.server";

describe("getDownloadKey", () => {
  it("returns a hex HMAC of the path and credential", () => {
    const key = getDownloadKey("/content/movie.mkv", "unit-test-key");
    expect(key).toMatch(/^[a-f0-9]{64}$/);
    expect(getDownloadKey("/content/movie.mkv", "unit-test-key")).toBe(key);
    expect(getDownloadKey("/content/other.mkv", "unit-test-key")).not.toBe(key);
    expect(getDownloadKey("/content/movie.mkv", "other-unit-test-key")).not.toBe(key);
  });
});
