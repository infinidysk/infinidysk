import { describe, expect, it, vi } from "vitest";

const { useOutletContext } = vi.hoisted(() => ({
  useOutletContext: vi.fn(),
}));

vi.mock("react-router", () => ({
  useOutletContext,
}));

import { useIsReadOnly } from "./authorization";

describe("useIsReadOnly", () => {
  it("is true only for the readonly role", () => {
    useOutletContext.mockReturnValue({
      role: "readonly",
      isOidcEnabled: false,
      serviceProvider: null,
    });
    expect(useIsReadOnly()).toBe(true);

    useOutletContext.mockReturnValue({
      role: "admin",
      isOidcEnabled: false,
      serviceProvider: null,
    });
    expect(useIsReadOnly()).toBe(false);
  });
});
