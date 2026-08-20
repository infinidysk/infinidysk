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
});
