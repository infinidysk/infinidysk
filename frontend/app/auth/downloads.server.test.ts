import { describe, expect, it, vi } from "vitest";
import { getDownloadKey } from "./downloads.server";

describe("getDownloadKey", () => {
  it("returns a hex HMAC of the path", () => {
    vi.stubEnv("FRONTEND_BACKEND_API_KEY", "unit-test-key");
    const key = getDownloadKey("/content/movie.mkv");
    expect(key).toMatch(/^[a-f0-9]{64}$/);
    expect(getDownloadKey("/content/movie.mkv")).toBe(key);
    expect(getDownloadKey("/content/other.mkv")).not.toBe(key);
    vi.unstubAllEnvs();
  });
});
