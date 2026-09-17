import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import type { HealthCheckStats } from "~/clients/backend-client.server";
import { HealthStats } from "./health-stats";

function render(stats: HealthCheckStats[]) {
  return renderToStaticMarkup(<HealthStats stats={stats} />);
}

describe("HealthStats", () => {
  it("shows a warning-toned Degraded card for degraded results", () => {
    const markup = render([
      { result: 0, repairStatus: 0, count: 6 },
      { result: 2, repairStatus: 0, count: 2 },
    ]);

    expect(markup).toContain("Degraded (25%)");
    expect(markup).toContain("text-warning");
  });

  it("reports zero degraded when no degraded results exist", () => {
    const markup = render([{ result: 0, repairStatus: 0, count: 4 }]);

    expect(markup).toContain("Degraded (0%)");
  });

  it("distinguishes recent check results from library configuration validation", () => {
    const markup = render([
      { result: 0, repairStatus: 0, count: 87 },
      { result: 1, repairStatus: 3, count: 13 },
    ]);

    expect(markup).toContain("Healthy (87%)");
    expect(markup).toContain("Action needed (13%)");
    expect(markup).toContain("A file can appear more than once");
    expect(markup).toContain("do not verify the Library Directory setting");
  });

  it("includes the omitted action-needed results from the reported overview", () => {
    const markup = render([
      { result: 0, repairStatus: 0, count: 11741 },
      { result: 1, repairStatus: 1, count: 455 },
      { result: 1, repairStatus: 2, count: 9 },
      { result: 1, repairStatus: 3, count: 9279 },
    ]);

    expect(markup).toContain("Healthy (55%)");
    expect(markup).toContain("Repaired (2%)");
    expect(markup).toContain("Deleted (0%)");
    expect(markup).toContain("Action needed (43%)");
    expectOverviewTotals(markup, [21484, 11741, 455, 9, 0, 9279]);
  });

  it("counts PAR2 repairs only as repaired, alongside Arr repairs", () => {
    const markup = render([
      { result: 0, repairStatus: 0, count: 4 },
      { result: 0, repairStatus: 4, count: 3 },
      { result: 1, repairStatus: 1, count: 1 },
      { result: 1, repairStatus: 2, count: 1 },
      { result: 2, repairStatus: 0, count: 1 },
    ]);

    expect(markup).toContain("Healthy (40%)");
    expect(markup).toContain("Repaired (40%)");
    expectOverviewTotals(markup, [10, 4, 4, 1, 1, 0]);
  });

  it("keeps unhealthy results without a repair action visible", () => {
    const markup = render([{ result: 1, repairStatus: 0, count: 2 }]);

    expect(markup).toContain("Action needed (100%)");
    expectOverviewTotals(markup, [2, 0, 0, 0, 0, 2]);
  });

  it("rounds equal shares deterministically to a complete breakdown", () => {
    const markup = render([
      { result: 0, repairStatus: 0, count: 1 },
      { result: 1, repairStatus: 1, count: 1 },
      { result: 1, repairStatus: 3, count: 1 },
    ]);

    expect(markup).toContain("Healthy (34%)");
    expect(markup).toContain("Repaired (33%)");
    expect(markup).toContain("Action needed (33%)");
    expectOverviewTotals(markup, [3, 1, 1, 0, 0, 1]);
  });

  it("shows zero counts and percentages for an empty period", () => {
    expectOverviewTotals(render([]), [0, 0, 0, 0, 0, 0]);
  });
});

function expectOverviewTotals(markup: string, expectedCounts: number[]) {
  const counts = [...markup.matchAll(/class="stat-value[^"]*">(\d+)</g)].map((match) =>
    Number(match[1]),
  );
  const percentages = [...markup.matchAll(/\((\d+)%\)/g)].map((match) => Number(match[1]));

  expect(counts).toEqual(expectedCounts);
  expect(counts.slice(1).reduce((sum, count) => sum + count, 0)).toBe(counts[0]);
  expect(percentages).toHaveLength(5);
  expect(percentages.reduce((sum, percentage) => sum + percentage, 0)).toBe(
    counts[0] === 0 ? 0 : 100,
  );
}
