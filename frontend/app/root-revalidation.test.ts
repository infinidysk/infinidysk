import { describe, expect, it } from "vitest";
import type { ShouldRevalidateFunctionArgs } from "react-router";
import { shouldRevalidate } from "./root-revalidation";

function args(partial: {
  current: string;
  next: string;
  formMethod?: ShouldRevalidateFunctionArgs["formMethod"];
  defaultShouldRevalidate?: boolean;
}): ShouldRevalidateFunctionArgs {
  return {
    currentUrl: new URL(`http://localhost${partial.current}`),
    nextUrl: new URL(`http://localhost${partial.next}`),
    currentParams: {},
    nextParams: {},
    defaultShouldRevalidate: partial.defaultShouldRevalidate ?? true,
    ...(partial.formMethod !== undefined ? { formMethod: partial.formMethod } : {}),
  };
}

describe("root shouldRevalidate", () => {
  it("revalidates when crossing the login/onboarding layout boundary", () => {
    expect(shouldRevalidate(args({ current: "/queue", next: "/login" }))).toBe(true);
    expect(shouldRevalidate(args({ current: "/login", next: "/overview" }))).toBe(true);
    expect(shouldRevalidate(args({ current: "/onboarding", next: "/queue" }))).toBe(true);
  });

  it("revalidates after settings POSTs", () => {
    expect(
      shouldRevalidate(args({ current: "/settings", next: "/settings", formMethod: "POST" })),
    ).toBe(true);
  });

  it("skips root revalidation for queue POSTs", () => {
    expect(shouldRevalidate(args({ current: "/queue", next: "/queue", formMethod: "POST" }))).toBe(
      false,
    );
  });

  it("skips same path and search revalidation", () => {
    expect(shouldRevalidate(args({ current: "/queue", next: "/queue" }))).toBe(false);
    expect(shouldRevalidate(args({ current: "/queue?qp=2", next: "/queue?qp=2" }))).toBe(false);
    expect(shouldRevalidate(args({ current: "/health", next: "/health" }))).toBe(false);
  });

  it("revalidates when the path changes", () => {
    expect(shouldRevalidate(args({ current: "/queue", next: "/overview" }))).toBe(true);
  });

  it("uses defaultShouldRevalidate when only search params change", () => {
    expect(
      shouldRevalidate(
        args({
          current: "/queue",
          next: "/queue?qp=2",
          defaultShouldRevalidate: true,
        }),
      ),
    ).toBe(true);
    expect(
      shouldRevalidate(
        args({
          current: "/queue",
          next: "/queue?qp=2",
          defaultShouldRevalidate: false,
        }),
      ),
    ).toBe(false);
  });
});
