import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import type { ThroughputPoint } from "~/clients/backend-client.server";
import { ThroughputChart } from "./throughput-chart";

const point = (articles: number, errors = 0): ThroughputPoint => ({
  bucket: 0,
  articles,
  misses: 0,
  errors,
  bytesServed: 0,
  bytesFetched: 0,
});

function render(points: ThroughputPoint[], totalErrors = 0) {
  return renderToStaticMarkup(
    <ThroughputChart
      points={points}
      totalArticles={points.reduce((sum, item) => sum + item.articles, 0)}
      totalMisses={0}
      totalErrors={totalErrors}
      totalBytesServed={0}
      bucketSizeMs={60_000}
      window="24h"
    />,
  );
}

function articlesPathD(markup: string): string {
  const match = markup.match(
    /d="([^"]*)"[^>]*data-series="articles"|data-series="articles"[^>]*d="([^"]*)"/,
  );
  return match?.[1] ?? match?.[2] ?? "";
}

describe("ThroughputChart", () => {
  it("does not draw the green series when every article bucket is zero", () => {
    const markup = render([point(0, 1), point(0)], 1);

    expect(markup).not.toContain('data-series="articles"');
    expect(markup).toContain('data-series="errors"');
  });

  it("draws the green series when an article bucket has activity", () => {
    const markup = render([point(0), point(2)]);

    expect(markup).toContain('data-series="articles"');
  });

  it("skips idle stretches but anchors each run to leading and trailing zeros", () => {
    const markup = render([point(0), point(5), point(0), point(0), point(3), point(0)]);
    const d = articlesPathD(markup);

    expect(d).not.toBe("");
    // Two non-zero spikes → two move commands (path breaks across idle zeros).
    expect((d.match(/M/g) ?? []).length).toBe(2);
    // Baseline y for this chart is 156.0 (VB_H - BOT_PAD); each run includes adjacent zeros.
    expect(d).toContain(",156.0");
    // First run: zero → 5 → zero. Peak y for articles=5 with scaleMax=5 is TOP_PAD (6.0).
    expect(d.startsWith("M0.0,156.0")).toBe(true);
    expect(d).toContain("L160.0,6.0");
    expect(d).toContain("L320.0,156.0");
  });
});
