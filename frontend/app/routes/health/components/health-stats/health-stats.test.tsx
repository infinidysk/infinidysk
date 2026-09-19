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

    expect(markup).toContain("Healthy (100%)");
    expect(markup).not.toContain("Action needed");
    expect(markup).toContain("A file can appear more than once");
    expect(markup).toContain("do not verify the Library Directory setting");
    expect(markup).toContain(
      "percentages cover only healthy, repaired, deleted, and degraded results",
    );
  });

  it("includes historical action-needed results in totals without displaying an actionable stat", () => {
    const markup = render([
      { result: 0, repairStatus: 0, count: 11741 },
      { result: 1, repairStatus: 1, count: 455 },
      { result: 1, repairStatus: 2, count: 9 },
      { result: 1, repairStatus: 3, count: 9279 },
    ]);

    expect(markup).toContain("Healthy (96.2%)");
    expect(markup).toContain("Repaired (3.7%)");
    expect(markup).toContain("Deleted (&lt;0.1%)");
    expectOverviewTotals(markup, [21484, 11741, 455, 9, 0], [96.2, 3.7, "&lt;0.1", 0]);
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
    expectOverviewTotals(markup, [10, 4, 4, 1, 1], [40, 40, 10, 10]);
  });

  it("includes unhealthy results without a repair action only in the total", () => {
    const markup = render([{ result: 1, repairStatus: 0, count: 2 }]);

    expectOverviewTotals(markup, [2, 0, 0, 0, 0], [0, 0, 0, 0]);
  });

  it("shows independent decimal shares without allocating a share to hidden failures", () => {
    const markup = render([
      { result: 0, repairStatus: 0, count: 1 },
      { result: 1, repairStatus: 1, count: 1 },
      { result: 2, repairStatus: 0, count: 1 },
      { result: 1, repairStatus: 3, count: 1 },
    ]);

    expect(markup).toContain("Healthy (33.3%)");
    expect(markup).toContain("Repaired (33.3%)");
    expectOverviewTotals(markup, [4, 1, 1, 0, 1], [33.3, 33.3, 0, 33.3]);
  });

  it("does not hide rare repaired or degraded outcomes behind 100 percent healthy", () => {
    const markup = render([
      { result: 0, repairStatus: 0, count: 11474 },
      { result: 1, repairStatus: 1, count: 29 },
      { result: 2, repairStatus: 0, count: 27 },
    ]);
    expect(markup).toContain("Healthy (99.5%)");
    expect(markup).toContain("Repaired (0.3%)");
    expect(markup).toContain("Degraded (0.2%)");
    expect(
      render([
        { result: 0, repairStatus: 0, count: 10000 },
        { result: 2, repairStatus: 0, count: 1 },
      ]),
    ).toContain("Healthy (&gt;99.9%)");
  });

  it("shows zero percentages when every result needs attention", () => {
    const markup = render([
      { result: 0, repairStatus: 3, count: 1 },
      { result: 2, repairStatus: 3, count: 1 },
      { result: 1, repairStatus: 3, count: 1 },
    ]);
    expectOverviewTotals(markup, [3, 0, 0, 0, 0], [0, 0, 0, 0]);
  });

  it("shows zero counts and percentages for an empty period", () => {
    expectOverviewTotals(render([]), [0, 0, 0, 0, 0], [0, 0, 0, 0]);
  });
});

function expectOverviewTotals(
  markup: string,
  expectedCounts: number[],
  expectedPercentages: (number | string)[],
) {
  const counts = [...markup.matchAll(/class="font-mono[^"]*">([\d,]+)</g)].map((match) =>
    Number(match[1]!.replaceAll(",", "")),
  );
  const percentages = [...markup.matchAll(/\(([^()]+)%\)/g)].map((match) => match[1]);

  expect(counts).toEqual(expectedCounts);
  expect(percentages).toEqual(expectedPercentages.map(String));
  expect(markup).not.toContain("Action needed");
}
