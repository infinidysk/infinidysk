import { afterEach, describe, expect, it, vi } from "vitest";
import { generateUuid } from "./uuid";

describe("generateUuid", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("uses crypto.randomUUID when available", () => {
    const randomUUID = vi.fn(() => "native-uuid");
    vi.stubGlobal("crypto", { randomUUID });

    expect(generateUuid()).toBe("native-uuid");
    expect(randomUUID).toHaveBeenCalledOnce();
  });

  it("generates a v4 UUID when randomUUID is unavailable", () => {
    vi.stubGlobal("crypto", {
      randomUUID: undefined,
      getRandomValues: (bytes: Uint8Array) => {
        bytes.fill(0);
        return bytes;
      },
    });

    expect(generateUuid()).toBe("00000000-0000-4000-8000-000000000000");
  });

  it("explains when no cryptographic random source is available", () => {
    vi.stubGlobal("crypto", undefined);

    expect(() => generateUuid()).toThrow(
      "This browser does not provide the cryptographic random source required to generate a UUID.",
    );
  });
});
