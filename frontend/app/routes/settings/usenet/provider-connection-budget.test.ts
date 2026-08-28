import { describe, expect, it } from "vitest";
import {
  calculateProviderConnectionBudget,
  formatMetadataCapacity,
} from "./provider-connection-budget";

describe("calculateProviderConnectionBudget", () => {
  it.each([
    [50, 20, 30, 10, 40],
    [50, 50, 0, 25, 25],
    [40, 16, 24, 8, 32],
    [43, 20, 23, 10, 33],
    [15, 15, 0, 7, 7],
    [10, 4, 6, 2, 8],
    [10, 5, 5, 2, 7],
    [1, 1, 0, 1, 1],
  ])(
    "derives metadata capacity for P=%i and T=%i",
    (providerLimit, transferLimit, base, burst, maximum) => {
      expect(calculateProviderConnectionBudget(providerLimit, transferLimit)).toEqual({
        providerLimit,
        transferLimit,
        baseMetadataCapacity: base,
        metadataBurstAllowance: burst,
        maxMetadataCapacity: maximum,
      });
    },
  );

  it.each([
    [0, 1],
    [-1, 1],
    [10, 0],
    [10, -1],
    [10, 11],
    [10.5, 5],
    [10, 5.5],
  ])("rejects an invalid P=%s and T=%s preview", (providerLimit, transferLimit) => {
    expect(calculateProviderConnectionBudget(providerLimit, transferLimit)).toBeNull();
  });
});

describe("formatMetadataCapacity", () => {
  it("formats the derived range with an en dash", () => {
    const budget = calculateProviderConnectionBudget(50, 20);

    expect(budget).not.toBeNull();
    expect(formatMetadataCapacity(budget!)).toBe("30\u201340 connections");
  });
});
