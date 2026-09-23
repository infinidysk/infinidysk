import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import type { ProviderSampledSpeedPoint } from "~/clients/backend-client.server";
import { ProviderSpeedChart } from "./provider-speed-chart";

const point = (speedMbPerSec: number | null): ProviderSampledSpeedPoint => ({
  bucket: 1_700_000_000_000,
  peakMbPerSec: speedMbPerSec,
  activeAverageMbPerSec: speedMbPerSec === null ? null : speedMbPerSec / 2,
});

function speedPathD(markup: string): string {
  const match = markup.match(
    /d="([^"]*)"[^>]*data-series="speed"|data-series="speed"[^>]*d="([^"]*)"/,
  );
  return match?.[1] ?? match?.[2] ?? "";
}

describe("ProviderSpeedChart", () => {
  it("leaves gaps for intervals without samples and renders both series", () => {
    const markup = renderToStaticMarkup(
      <ProviderSpeedChart
        providerLabel="Alpha"
        points={[point(4), point(null), point(7)]}
        bucketSizeMs={60_000}
        historyTruncated={false}
        window="1h"
      />,
    );
    const d = speedPathD(markup);

    expect(d).not.toBe("");
    expect((d.match(/M/g) ?? []).length).toBe(2);
    expect(d).not.toContain("400.0,156.0");
    expect(markup).toContain('data-series="active-average"');
    expect(markup).toContain("Active avg");
    expect(d.startsWith("M0.0,")).toBe(true);
  });

  it("keeps a terminal isolated sample inside the viewBox", () => {
    const markup = renderToStaticMarkup(
      <ProviderSpeedChart
        providerLabel="Alpha"
        points={[point(null), point(null), point(7)]}
        bucketSizeMs={60_000}
        historyTruncated={false}
        window="1h"
      />,
    );
    const d = speedPathD(markup);
    const xs = [...d.matchAll(/[ML]([\d.]+),/g)].map((match) => Number(match[1]));

    expect(d).not.toBe("");
    expect(xs.length).toBeGreaterThan(0);
    expect(Math.min(...xs)).toBeGreaterThanOrEqual(0);
    expect(Math.max(...xs)).toBeLessThanOrEqual(800);
    expect(Math.min(...xs)).toBeLessThan(800);
    expect(d).not.toContain("0.0,156.0");
    expect(d).not.toContain("400.0,156.0");
  });

  it("centers a single-bucket sample on the chart", () => {
    const markup = renderToStaticMarkup(
      <ProviderSpeedChart
        providerLabel="Alpha"
        points={[point(7)]}
        bucketSizeMs={60_000}
        historyTruncated={false}
        window="1h"
      />,
    );
    const d = speedPathD(markup);
    const xs = [...d.matchAll(/[ML]([\d.]+),/g)].map((match) => Number(match[1]));

    expect(d).not.toBe("");
    expect(xs.length).toBe(2);
    expect(Math.min(...xs)).toBeGreaterThan(0);
    expect(Math.max(...xs)).toBeLessThan(800);
    expect(xs.some((x) => x < 400)).toBe(true);
    expect(xs.some((x) => x > 400)).toBe(true);
  });
});
