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
    expect(markup.match(/Repairing for 12m/g)).toHaveLength(2);
    expect(markup).toContain("not a countdown");
  });

  it("offers stop only on active rows when a stop handler is provided", () => {
    const items = [
      {
        id: "active",
        name: "active.mkv",
        path: "/content/active.mkv",
        releaseDate: null,
        lastHealthCheck: null,
        nextHealthCheck: null,
        countsTowardUncheckedCount: true,
        progress: 10,
      },
      {
        id: "queued",
        name: "queued.mkv",
        path: "/content/queued.mkv",
        releaseDate: null,
        lastHealthCheck: null,
        nextHealthCheck: null,
        countsTowardUncheckedCount: true,
      },
    ];
    const withStop = renderToStaticMarkup(
      <HealthTable isEnabled healthCheckItems={items} onStop={() => {}} />,
    );
    const withoutStop = renderToStaticMarkup(<HealthTable isEnabled healthCheckItems={items} />);

    expect(withStop.match(/aria-label="Stop health check for active.mkv"/g)).toHaveLength(2);
    expect(withStop).not.toContain("Stop health check for queued.mkv");
    expect(withoutStop).not.toContain("Stop health check");
  });

  it("disables the stop control while its stop request is pending", () => {
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
            progress: 10,
          },
        ]}
        onStop={() => {}}
        stoppingIds={new Set(["active"])}
      />,
    );

    const buttons = markup.match(
      /<button[^>]*aria-label="Stop health check for active\.mkv"[^>]*>/g,
    );
    expect(buttons).toHaveLength(2);
    for (const button of buttons ?? []) expect(button).toContain('disabled=""');
  });
});
