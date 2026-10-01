import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { HealthTable } from "./health-table";

describe("HealthTable", () => {
  it.each([0, 35])("shows active progress at %i percent on desktop and mobile", (progress) => {
    const markup = renderToStaticMarkup(
      <HealthTable
        isEnabled
        healthCheckItems={[
          {
            id: "active",
            name: "active.mkv",
            path: "/content/active.mkv",
            releaseDate: null,
            lastHealthCheck: null,
            nextHealthCheck: null,
            countsTowardUncheckedCount: true,
            progress,
          },
        ]}
      />,
    );

    expect(markup.match(/<progress /g)).toHaveLength(2);
    expect(markup).toContain(`${progress}%`);
    expect(markup).not.toContain("ASAP");
  });

  it("shows the phase and elapsed time instead of a frozen percentage outside the STAT sweep", () => {
    const markup = renderToStaticMarkup(
      <HealthTable
        isEnabled
        healthCheckItems={[
          {
            id: "repairing",
            name: "repairing.mkv",
            path: "/content/repairing.mkv",
            releaseDate: null,
            lastHealthCheck: null,
            nextHealthCheck: null,
            countsTowardUncheckedCount: true,
            progress: 0,
            phase: "Repairing",
            phaseStartedAt: new Date(Date.now() - 12 * 60_000).toISOString(),
          },
        ]}
      />,
    );

    expect(markup).not.toContain("<progress ");
    expect(markup).not.toContain("0%");
    expect(markup.match(/Repairing · 12m/g)).toHaveLength(2);
  });
});
