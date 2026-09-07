import { describe, expect, it } from "vitest";
import {
  EMPTY_OVERVIEW_STATS,
  mergeOverviewStats,
  mergeProviderCircuitBreakers,
} from "./merge-overview-stats";
import type { OverviewStatsResponse } from "~/clients/backend-client.server";

function partial(
  overrides: Partial<OverviewStatsResponse> & { includedSections: string[] },
): OverviewStatsResponse {
  return { ...EMPTY_OVERVIEW_STATS, ...overrides };
}

describe("mergeOverviewStats", () => {
  it("merges window section without wiping static data", () => {
    const withStatic = mergeOverviewStats(
      EMPTY_OVERVIEW_STATS,
      partial({
        includedSections: ["static"],
        catalogue: { fileCount: 42, totalBytes: 100, largestFileBytes: 50, addedLast7Days: 3 },
        indexers: [
          {
            name: "NZB",
            completed: 1,
            failed: 0,
            bytesCompleted: 10,
            avgSeconds: 2,
            successRate: 1,
          },
        ],
      }),
    );

    const withWindow = mergeOverviewStats(
      withStatic,
      partial({
        includedSections: ["window"],
        totalArticles: 99,
        totalMisses: 40,
        totalErrors: 2,
        throughput: [
          { bucket: 1, articles: 5, misses: 2, errors: 0, bytesServed: 10, bytesFetched: 20 },
        ],
        tiles: {
          activeReads: 2,
          articlesPerMinute: 10,
          errorsPerMinute: 0,
          bytesServedPerMinute: 1000,
        },
      }),
    );

    expect(withWindow.totalArticles).toBe(99);
    expect(withWindow.totalMisses).toBe(40);
    expect(withWindow.totalErrors).toBe(2);
    expect(withWindow.throughput).toHaveLength(1);
    expect(withWindow.catalogue.fileCount).toBe(42);
    expect(withWindow.indexers).toHaveLength(1);
    expect(withWindow.includedSections).toEqual(expect.arrayContaining(["static", "window"]));
  });

  it("merges detail latency without clearing window throughput", () => {
    const withWindow = mergeOverviewStats(
      EMPTY_OVERVIEW_STATS,
      partial({
        includedSections: ["window"],
        throughput: [
          { bucket: 1, articles: 5, misses: 1, errors: 0, bytesServed: 10, bytesFetched: 20 },
        ],
      }),
    );

    const withDetail = mergeOverviewStats(
      withWindow,
      partial({
        includedSections: ["detail"],
        latency: { p50Ms: 12, p95Ms: 40, p99Ms: 90, samples: 100, buckets: [] },
        errors: [{ status: "Missing", count: 3 }],
      }),
    );

    expect(withDetail.throughput).toHaveLength(1);
    expect(withDetail.latency.p50Ms).toBe(12);
    expect(withDetail.errors).toEqual([{ status: "Missing", count: 3 }]);
  });

  it("merges live breaker updates without wiping historical provider columns", () => {
    const providers = mergeProviderCircuitBreakers(
      [
        {
          provider: "11111111-1111-1111-1111-111111111111",
          nickname: "Primary",
          articles: 12,
          bytesFetched: 1000,
          errors: 1,
          retries: 0,
          avgDurationMs: 40,
          errorRate: 0.08,
          spark: [1, 2],
          errorSpark: [0, 1],
          retrySpark: [2, 0],
          outageSpark: [0, 50],
        },
      ],
      [
        {
          provider: "11111111-1111-1111-1111-111111111111",
          nickname: "Primary",
          circuitState: "open",
          cooldownRemainingSeconds: 30,
          lastFailureReason: "3 failures in 3-sample window",
          tripCount: 1,
          failureCount: 3,
          articleMissCount: 0,
        },
      ],
    );

    expect(providers[0]?.articles).toBe(12);
    expect(providers[0]?.errorSpark).toEqual([0, 1]);
    expect(providers[0]?.retrySpark).toEqual([2, 0]);
    expect(providers[0]?.outageSpark).toEqual([0, 50]);
    expect(providers[0]?.circuitState).toBe("open");
    expect(providers[0]?.cooldownRemainingSeconds).toBe(30);
  });
});
