// @vitest-environment jsdom
import { cleanup, fireEvent, render } from "@testing-library/react";
import { renderToStaticMarkup } from "react-dom/server";
import { afterEach, describe, expect, it } from "vitest";
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

function renderMarkup(points: ThroughputPoint[], totalErrors = 0) {
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
  afterEach(() => {
    cleanup();
  });
  it("does not draw the green series when every article bucket is zero", () => {
    const markup = renderMarkup([point(0, 1), point(0)], 1);

    expect(markup).not.toContain('data-series="articles"');
    expect(markup).toContain('data-series="errors"');
  });

  it("draws the green series when an article bucket has activity", () => {
    const markup = renderMarkup([point(0), point(2)]);

    expect(markup).toContain('data-series="articles"');
  });

  it("skips idle stretches but anchors each run to leading and trailing zeros", () => {
    const markup = renderMarkup([point(0), point(5), point(0), point(0), point(3), point(0)]);
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

  it("announces keyboard-selected bucket details to assistive tech", () => {
    const points = [
      { ...point(3), bucket: 1 },
      {
        ...point(8),
        bucket: 2,
        misses: 1,
        errors: 2,
        bytesServed: 100,
        bytesFetched: 50,
      },
    ];
    const { container, rerender } = render(
      <ThroughputChart
        points={points}
        totalArticles={11}
        totalMisses={1}
        totalErrors={2}
        totalBytesServed={100}
        bucketSizeMs={60_000}
        window="24h"
      />,
    );

    const chart = container.querySelector('[role="img"]');
    expect(chart).toBeInstanceOf(HTMLElement);
    (chart as HTMLElement).focus();
    expect(document.activeElement).toBe(chart);

    fireEvent.keyDown(chart!, { key: "ArrowRight" });
    fireEvent.keyDown(chart!, { key: "ArrowRight" });

    const status = container.querySelector("#overview-throughput-keyboard-status");
    expect(status?.textContent).toMatch(/8 articles/);
    expect(status?.textContent).toMatch(/2 errors/);

    const updated = [points[0]!, { ...points[1]!, articles: 12, errors: 4 }];
    rerender(
      <ThroughputChart
        points={updated}
        totalArticles={15}
        totalMisses={1}
        totalErrors={4}
        totalBytesServed={100}
        bucketSizeMs={60_000}
        window="24h"
      />,
    );
    expect(status?.textContent).toMatch(/12 articles/);
    expect(status?.textContent).toMatch(/4 errors/);
  });
});
