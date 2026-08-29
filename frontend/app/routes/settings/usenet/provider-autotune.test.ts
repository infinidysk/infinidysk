import { describe, expect, it } from "vitest";
import {
  applyAutoTuneTransferRecommendation,
  resolveBenchmarkConnectionLimits,
} from "./provider-autotune";

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

describe("resolveBenchmarkConnectionLimits", () => {
  it("uses the provider ceiling for a full sweep even when the transfer draft is invalid", () => {
    expect(
      resolveBenchmarkConnectionLimits(
        { providerConnectionLimit: "5", transferConnections: "10" },
        false,
      ),
    ).toEqual({
      providerConnectionLimit: "5",
      testConnections: "5",
    });
  });

  it.each(["", "0", "-1", "not-a-number"])(
    "rejects an invalid provider ceiling (%s)",
    (providerConnectionLimit) => {
      expect(
        resolveBenchmarkConnectionLimits(
          { providerConnectionLimit, transferConnections: "" },
          false,
        ),
      ).toBeNull();
    },
  );

  it("uses valid transfer connections for a pipelining-only run", () => {
    expect(
      resolveBenchmarkConnectionLimits(
        { providerConnectionLimit: "20", transferConnections: "8" },
        true,
      ),
    ).toEqual({
      providerConnectionLimit: "20",
      testConnections: "8",
    });
  });

  it("falls back to the valid provider ceiling when transfer connections are blank", () => {
    expect(
      resolveBenchmarkConnectionLimits(
        { providerConnectionLimit: "20", transferConnections: "" },
        true,
      ),
    ).toEqual({
      providerConnectionLimit: "20",
      testConnections: "20",
    });
  });

  it.each(["0", "-1", "not-a-number", "21"])(
    "rejects invalid pipelining-only transfer connections (%s)",
    (transferConnections) => {
      expect(
        resolveBenchmarkConnectionLimits(
          { providerConnectionLimit: "20", transferConnections },
          true,
        ),
      ).toBeNull();
    },
  );
});
