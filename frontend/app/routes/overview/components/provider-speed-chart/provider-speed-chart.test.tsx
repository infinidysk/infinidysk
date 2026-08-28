import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import type { ProviderSpeedPoint } from "~/clients/backend-client.server";
import { ProviderSpeedChart } from "./provider-speed-chart";

const point = (speedMbPerSec: number): ProviderSpeedPoint => ({
  bucket: 1_700_000_000_000,
  speedMbPerSec,
  bytesFetched: speedMbPerSec > 0 ? 1_000 : 0,
});

function speedPathD(markup: string): string {
  const match = markup.match(
    /d="([^"]*)"[^>]*data-series="speed"|data-series="speed"[^>]*d="([^"]*)"/,
  );
  return match?.[1] ?? match?.[2] ?? "";
}

describe("ProviderSpeedChart", () => {
  it("does not connect positive runs through a single idle bucket", () => {
    const markup = renderToStaticMarkup(
      <ProviderSpeedChart
        providerLabel="Alpha"
        points={[point(4), point(0), point(7)]}
        bucketSizeMs={60_000}
        historyTruncated={false}
        window="1h"
      />,
    );
    const d = speedPathD(markup);

    expect(d).not.toBe("");
    expect((d.match(/M/g) ?? []).length).toBe(2);
    // Three buckets span 800 viewBox units, so the idle bucket is at x=400.
    expect(d).not.toContain("400.0,");
  });

  it("keeps a terminal isolated sample inside the viewBox", () => {
    const markup = renderToStaticMarkup(
      <ProviderSpeedChart
        providerLabel="Alpha"
        points={[point(0), point(0), point(7)]}
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
  });
});
