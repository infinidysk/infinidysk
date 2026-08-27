import { describe, expect, it } from "vitest";
import { applyAutoTuneTransferRecommendation } from "./provider-autotune";

describe("applyAutoTuneTransferRecommendation", () => {
  const draft = {
    providerConnectionLimit: "50",
    transferConnections: "30",
  } as const;

  it("applies the knee only to transfer connections", () => {
    const applied = applyAutoTuneTransferRecommendation(draft, 20, false, false);

    expect(applied).toEqual({
      providerConnectionLimit: "50",
      transferConnections: "20",
    });
  });

  it("caps a stale recommendation at the current provider limit", () => {
    const applied = applyAutoTuneTransferRecommendation(
      { providerConnectionLimit: "12", transferConnections: "10" },
      20,
      false,
      false,
    );

    expect(applied.transferConnections).toBe("12");
  });

  it("does not change limits for a pipelining-only result", () => {
    expect(applyAutoTuneTransferRecommendation(draft, 20, true, false)).toBe(draft);
  });

  it("does not change limits for a verification result", () => {
    expect(applyAutoTuneTransferRecommendation(draft, 20, false, true)).toBe(draft);
  });

  it.each([null, undefined, 0, -1, 1.5])(
    "ignores a non-applicable recommendation (%s)",
    (recommendation) => {
      expect(applyAutoTuneTransferRecommendation(draft, recommendation, false, false)).toBe(draft);
    },
  );
});
