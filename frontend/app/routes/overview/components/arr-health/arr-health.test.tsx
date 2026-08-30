import { renderToStaticMarkup } from "react-dom/server";
import { MemoryRouter } from "react-router";
import { describe, expect, it } from "vitest";
import type { ArrHealthResponse } from "~/clients/backend-client.server";
import { ArrHealth } from "./arr-health";

const emptySummary = {
  instancesOnline: 0,
  instancesTotal: 0,
  importsCompleted: 0,
  medianHandoffMs: null,
  p95HandoffMs: null,
  awaitingImport: 0,
  awaitingShown: 0,
  degraded: 0,
};

function render(data: ArrHealthResponse, window: "24h" | "all" = "24h") {
  return renderToStaticMarkup(
    <MemoryRouter>
      <ArrHealth data={data} window={window} />
    </MemoryRouter>,
  );
}

describe("ArrHealth", () => {
  it("colors instance names as status badges and keeps metric icon headers", () => {
    const markup = render({
      configured: true,
      summary: {
        instancesOnline: 2,
        instancesTotal: 4,
        importsCompleted: 121,
        medianHandoffMs: 7800,
        p95HandoffMs: 21000,
        awaitingImport: 3,
        awaitingShown: 0,
        degraded: 1,
      },
      instances: [
        {
          key: "sonarr|http://sonarr:8989",
          name: "Sonarr Main",
          appType: "sonarr",
          host: "http://sonarr:8989",
          status: "healthy",
          imports: 121,
          medianHandoffMs: 7800,
          p95HandoffMs: 21000,
          queueCount: 0,
          awaitingCount: 0,
          hasWarnings: false,
          hasErrors: false,
          lastImportAtMs: Date.now() - 180_000,
          lastError: null,
        },
        {
          key: "sonarr|http://sonarr-4k:8989",
          name: "Sonarr 4K",
          appType: "sonarr",
          host: "http://sonarr-4k:8989",
          status: "degraded",
          imports: 29,
          medianHandoffMs: 41000,
          p95HandoffMs: 138000,
          queueCount: 3,
          awaitingCount: 3,
          hasWarnings: false,
          hasErrors: false,
          lastImportAtMs: Date.now() - 19 * 60_000,
          lastError: null,
        },
        {
          key: "radarr|http://radarr:7878",
          name: "Radarr",
          appType: "radarr",
          host: "http://radarr:7878",
          status: "offline",
          imports: 0,
          medianHandoffMs: null,
          p95HandoffMs: null,
          queueCount: 0,
          awaitingCount: 0,
          hasWarnings: false,
          hasErrors: false,
          lastImportAtMs: null,
          lastError: "Unreachable",
        },
        {
          key: "radarr|http://radarr-new:7878",
          name: "Radarr New",
          appType: "radarr",
          host: "http://radarr-new:7878",
          status: "pending",
          imports: 0,
          medianHandoffMs: null,
          p95HandoffMs: null,
          queueCount: 0,
          awaitingCount: 0,
          hasWarnings: false,
          hasErrors: false,
          lastImportAtMs: null,
          lastError: null,
        },
      ],
      awaiting: [],
    });

    expect(markup).toContain("Arr Health");
    expect(markup).toContain("2/4");
    expect(markup).toContain("7.8s");
    expect(markup).toContain("21s");
    expect(markup).toContain("badge-success");
    expect(markup).toContain("badge-warning");
    expect(markup).toContain("badge-error");
    expect(markup).toContain("badge-ghost");
    expect(markup).toContain("Sonarr Main, healthy");
    expect(markup).toContain("Sonarr 4K, degraded");
    expect(markup).toContain("Radarr, offline");
    expect(markup).toContain("Radarr New, pending");
    expect(markup).not.toMatch(/>healthy</);
    expect(markup).toContain("Unreachable");
    expect(markup).toContain("max-sm:w-[42%]");
    expect(markup).toContain("max-sm:w-[11%]");
    expect(markup).toContain("max-sm:w-[25%]");
    expect(markup).toMatch(/max-sm:!inline-block[^>]*>download</);
    expect(markup).toMatch(/max-sm:!inline-block[^>]*>queue</);
    expect(markup).toMatch(/max-sm:!inline-block[^>]*>pending</);
    expect(markup).toMatch(/max-sm:!inline-block[^>]*>schedule</);
    expect(markup).toContain("tooltip-end");
    expect(markup).toContain("max-sm:w-52!");
    expect(markup).toContain("max-sm:sr-only");
    expect(markup).not.toContain("overflow-x-auto");
    expect(markup).not.toContain("min-w-[640px]");
  });

  it("shows the empty state when no instances have imported yet", () => {
    const markup = render({
      configured: true,
      summary: emptySummary,
      instances: [],
      awaiting: [],
    });
    expect(markup).toContain("No imports recorded yet.");
  });

  it("highlights unusually long awaits and shows an em dash for unknown waits", () => {
    const markup = render({
      configured: true,
      summary: {
        ...emptySummary,
        instancesOnline: 1,
        instancesTotal: 1,
        awaitingImport: 2,
        awaitingShown: 2,
      },
      instances: [
        {
          key: "sonarr|http://sonarr:8989",
          name: "Sonarr Main",
          appType: "sonarr",
          host: "http://sonarr:8989",
          status: "degraded",
          imports: 0,
          medianHandoffMs: 8000,
          p95HandoffMs: 20000,
          queueCount: 2,
          awaitingCount: 2,
          hasWarnings: false,
          hasErrors: false,
          lastImportAtMs: null,
          lastError: null,
        },
      ],
      awaiting: [
        {
          title: "Example Show S04E06",
          downloadId: null,
          instanceKey: "sonarr|http://sonarr:8989",
          instanceName: "Sonarr Main",
          waitingMs: 47 * 60_000,
          isUnusual: true,
          trackedDownloadState: "importPending",
          statusReason: "Invalid data found when processing input",
        },
        {
          title: "Example Show S04E06",
          downloadId: null,
          instanceKey: "sonarr|http://sonarr:8989",
          instanceName: "Sonarr Main",
          waitingMs: null,
          isUnusual: false,
          trackedDownloadState: null,
          statusReason: null,
        },
      ],
    });

    expect(markup).toContain("Example Show S04E06");
    expect(markup).toContain("unusually long");
    expect(markup).toContain("text-warning");
    expect(markup).toContain("waiting —");
    expect(markup).toContain("2 of 2 longest waits");
    expect(markup).toContain("Invalid data found when processing input");
    expect(markup).not.toContain("2 affected items");
  });

  it("notes the 90-day retention on the All window", () => {
    const markup = render(
      {
        configured: true,
        summary: emptySummary,
        instances: [],
        awaiting: [],
      },
      "all",
    );
    expect(markup).toContain("~90 days of stored events");
  });
});
