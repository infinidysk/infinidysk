// @vitest-environment jsdom
import { cleanup, fireEvent, render } from "@testing-library/react";
import { renderToStaticMarkup } from "react-dom/server";
import { afterEach, describe, expect, it } from "vitest";
import type { ThroughputPoint } from "~/clients/backend-client.server";
import { formatBytes } from "../../utils/format";
import { ThroughputChart } from "./throughput-chart";

const point = (
  articles: number,
  clientArticles = 0,
  errors = 0,
  queueArticles = 0,
): ThroughputPoint => ({
  bucket: 0,
  articles,
  clientArticles,
  queueArticles,
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
      totalClientArticles={points.reduce((sum, item) => sum + item.clientArticles, 0)}
      totalQueueArticles={points.reduce((sum, item) => sum + item.queueArticles, 0)}
      totalMisses={0}
      totalErrors={totalErrors}
      totalBytesServed={0}
      totalBytesFetched={0}
      bucketSizeMs={60_000}
      window="24h"
    />,
  );
}

function articlesPathD(markup: string): string {
  const match = markup.match(
    /d="([^"]*)"[^>]*data-series="client-articles"|data-series="client-articles"[^>]*d="([^"]*)"/,
  );
  return match?.[1] ?? match?.[2] ?? "";
}

describe("ThroughputChart", () => {
  afterEach(() => {
    cleanup();
  });
  it("does not draw the green series when every article bucket is zero", () => {
    const markup = renderMarkup([point(0, 0, 1), point(0)], 1);

    expect(markup).not.toContain('data-series="client-articles"');
    expect(markup).not.toContain('data-series="queue-articles"');
    expect(markup).not.toContain('data-series="app-articles"');
    expect(markup).toContain('data-series="errors"');
  });

  it("draws the green series when an article bucket has activity", () => {
    const markup = renderMarkup([point(0), point(2, 2)]);

    expect(markup).toContain('data-series="client-articles"');
  });

  it("uses a solid blue swatch for maintenance reads in the legend", () => {
    const markup = renderMarkup([point(3, 1)]);

    expect(markup).toContain("Maintenance attempts · 2");
    expect(markup).toContain("border-t-2 border-info");
    expect(markup).not.toContain("border-dashed");
  });

  it("splits import attempts into a violet series distinct from client and maintenance", () => {
    // 10 attempts: 3 client, 5 import, 2 residual maintenance.
    const markup = renderMarkup([point(0), point(10, 3, 0, 5)]);

    expect(markup).toContain('data-series="client-articles"');
    expect(markup).toContain('data-series="queue-articles"');
    expect(markup).toContain('data-series="app-articles"');
    expect(markup).toContain("Client attempts · 3");
    expect(markup).toContain("Import attempts · 5");
    expect(markup).toContain("Maintenance attempts · 2");
    expect(markup).toContain("bg-secondary");
    expect(markup).toContain("3 client attempts, 5 import attempts, 2 maintenance attempts");
  });

  it("treats buckets recorded before import tracking as maintenance and clamps overlaps", () => {
    // Legacy bucket: queueArticles missing/zero → everything non-client stays blue.
    const legacy = renderMarkup([point(4, 1)]);
    expect(legacy).not.toContain('data-series="queue-articles"');
    expect(legacy).toContain("Import attempts · 0");
    expect(legacy).toContain("Maintenance attempts · 3");

    // Over-reported import count is clamped to what is left after client attempts.
    const clamped = renderMarkup([point(4, 3, 0, 9)]);
    expect(clamped).toContain("Import attempts · 1");
    expect(clamped).toContain("Maintenance attempts · 0");
    expect(clamped).not.toContain('data-series="app-articles"');
  });

  it("labels the y-axis with the error-dominant coordinate scale", () => {
    const markup = renderMarkup([point(2, 2, 10)], 10);

    expect(markup).toContain(">10</span>");
    expect(markup).toContain(">5</span>");
  });

  it("keeps aggregate download throughput neutral instead of labeling it as app reads", () => {
    const markup = renderToStaticMarkup(
      <ThroughputChart
        points={[
          {
            ...point(10, 10, 0),
            bucket: 0,
            bytesFetched: 60 * 1024 * 1024,
            bytesServed: 0,
          },
        ]}
        totalArticles={10}
        totalClientArticles={10}
        totalQueueArticles={0}
        totalMisses={0}
        totalErrors={0}
        totalBytesServed={0}
        totalBytesFetched={60 * 1024 * 1024}
        bucketSizeMs={60_000}
        window="24h"
      />,
    );

    expect(markup).toContain("Client attempts · 10");
    expect(markup).toContain("Peak download");
    expect(markup).not.toContain("bg-base-content/40");
    expect(markup).not.toContain("Maintenance attempts · 0 · peak");
  });

  it("skips idle stretches but anchors each run to leading and trailing zeros", () => {
    const markup = renderMarkup([point(0), point(5, 5), point(0), point(0), point(3, 3), point(0)]);
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
        totalClientArticles={0}
        totalQueueArticles={0}
        totalMisses={1}
        totalErrors={2}
        totalBytesServed={100}
        totalBytesFetched={50}
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
    expect(status?.textContent).toMatch(/8 attempts/);
    expect(status?.textContent).toMatch(/2 errors/);

    const updated = [points[0]!, { ...points[1]!, articles: 12, errors: 4 }];
    rerender(
      <ThroughputChart
        points={updated}
        totalArticles={15}
        totalClientArticles={0}
        totalQueueArticles={0}
        totalMisses={1}
        totalErrors={4}
        totalBytesServed={100}
        totalBytesFetched={50}
        bucketSizeMs={60_000}
        window="24h"
      />,
    );
    expect(status?.textContent).toMatch(/12 attempts/);
    expect(status?.textContent).toMatch(/4 errors/);
  });

  it("keeps hover and keyboard cursors on the same bucket after polling prepends a point", () => {
    const points = [
      { ...point(3), bucket: 1 },
      { ...point(8), bucket: 2, errors: 2 },
    ];
    const { container, rerender } = render(
      <ThroughputChart
        points={points}
        totalArticles={11}
        totalClientArticles={0}
        totalQueueArticles={0}
        totalMisses={0}
        totalErrors={2}
        totalBytesServed={0}
        totalBytesFetched={0}
        bucketSizeMs={60_000}
        window="24h"
      />,
    );

    const chart = container.querySelector('[role="img"]');
    expect(chart).toBeInstanceOf(HTMLElement);
    (chart as HTMLElement).focus();
    fireEvent.keyDown(chart!, { key: "ArrowRight" });
    fireEvent.keyDown(chart!, { key: "ArrowRight" });

    const status = container.querySelector("#overview-throughput-keyboard-status");
    expect(status?.textContent).toMatch(/8 attempts/);

    const shifted = [{ ...point(1), bucket: 0 }, points[0]!, points[1]!];
    rerender(
      <ThroughputChart
        points={shifted}
        totalArticles={12}
        totalClientArticles={0}
        totalQueueArticles={0}
        totalMisses={0}
        totalErrors={2}
        totalBytesServed={0}
        totalBytesFetched={0}
        bucketSizeMs={60_000}
        window="24h"
      />,
    );

    expect(status?.textContent).toMatch(/8 attempts/);
    expect(status?.textContent).toMatch(/2 errors/);

    fireEvent.keyDown(chart!, { key: "ArrowLeft" });
    expect(status?.textContent).toMatch(/3 attempts/);
    expect(status?.textContent).not.toMatch(/8 attempts/);
  });

  it.each([
    { articles: 100, misses: 30, errors: 10, success: "60" },
    { articles: 0, misses: 0, errors: 0, success: "0" },
    { articles: 10, misses: 8, errors: 2, success: "0" },
  ])(
    "shows successful reads $success excluding misses and errors",
    ({ articles, misses, errors, success }) => {
      const { getByText, queryByText } = render(
        <ThroughputChart
          points={[]}
          totalArticles={articles}
          totalClientArticles={0}
          totalQueueArticles={0}
          totalMisses={misses}
          totalErrors={errors}
          totalBytesServed={0}
          totalBytesFetched={0}
          bucketSizeMs={60_000}
          window="24h"
        />,
      );

      expect(getByText("Successful reads").parentElement?.textContent).toBe(
        `Successful reads${success}`,
      );
      expect(getByText("Peak download").parentElement?.textContent).toBe("Peak downloadN/A");
      expect(queryByText("Cache share")).toBeNull();
      expect(queryByText("Misses")).toBeNull();
      expect(queryByText("Articles")).toBeNull();
      fireEvent.focus(getByText("Peak download").parentElement!);
      expect(getByText(/Highest 1-second Usenet download rate/).getAttribute("aria-hidden")).toBe(
        "false",
      );
    },
  );

  it.each([60_000, 3_600_000, 86_400_000])(
    "normalizes peak download for %d ms buckets without a duplicate legend item",
    (bucketSizeMs) => {
      const { getByText, getAllByText } = render(
        <ThroughputChart
          points={[
            { ...point(3), bytesFetched: (1_000_000 * bucketSizeMs) / 1000 },
            { ...point(5), bucket: bucketSizeMs, bytesFetched: (2_000_000 * bucketSizeMs) / 1000 },
          ]}
          totalArticles={8}
          totalClientArticles={0}
          totalQueueArticles={0}
          totalMisses={0}
          totalErrors={0}
          totalBytesServed={0}
          totalBytesFetched={0}
          bucketSizeMs={bucketSizeMs}
          window={bucketSizeMs === 60_000 ? "24h" : bucketSizeMs === 3_600_000 ? "7d" : "all"}
        />,
      );
      expect(getAllByText("Peak download")).toHaveLength(1);
      expect(getByText("Peak download").parentElement?.textContent).toBe("Peak download2.0 MB/s");
    },
  );

  it.each([60_000, 3_600_000, 86_400_000])(
    "prefers the sampled 1-second peak over bucket averages for %d ms buckets",
    (bucketSizeMs) => {
      const { getByText } = render(
        <ThroughputChart
          points={[
            { ...point(3), bytesFetched: (1_000_000 * bucketSizeMs) / 1000 },
            { ...point(5), bucket: bucketSizeMs, bytesFetched: (2_000_000 * bucketSizeMs) / 1000 },
          ]}
          totalArticles={8}
          totalClientArticles={0}
          totalQueueArticles={0}
          totalMisses={0}
          totalErrors={0}
          totalBytesServed={0}
          totalBytesFetched={0}
          bucketSizeMs={bucketSizeMs}
          peakFetchBytesPerSec={109_000_000}
          window={bucketSizeMs === 60_000 ? "24h" : bucketSizeMs === 3_600_000 ? "7d" : "all"}
        />,
      );
      expect(getByText("Peak download").parentElement?.textContent).toBe("Peak download109 MB/s");
    },
  );

  it("falls back to the bucket average when the sampled peak is lower", () => {
    const { getByText } = render(
      <ThroughputChart
        points={[{ ...point(3), bytesFetched: 2_000_000 * 60 }]}
        totalArticles={3}
        totalClientArticles={0}
        totalQueueArticles={0}
        totalMisses={0}
        totalErrors={0}
        totalBytesServed={0}
        totalBytesFetched={0}
        bucketSizeMs={60_000}
        peakFetchBytesPerSec={1_000_000}
        window="24h"
      />,
    );
    expect(getByText("Peak download").parentElement?.textContent).toBe("Peak download2.0 MB/s");
  });

  it("shows provider bytes fetched separately from bytes served", () => {
    const { getByText } = render(
      <ThroughputChart
        points={[]}
        totalArticles={0}
        totalClientArticles={0}
        totalQueueArticles={0}
        totalMisses={0}
        totalErrors={0}
        totalBytesServed={1_000_000_000}
        totalBytesFetched={2_500_000_000}
        bucketSizeMs={60_000}
        window="24h"
      />,
    );

    expect(getByText("Served").parentElement?.textContent).toBe(
      `Served${formatBytes(1_000_000_000)}`,
    );
    expect(getByText("Fetched").parentElement?.textContent).toBe(
      `Fetched${formatBytes(2_500_000_000)}`,
    );
    fireEvent.focus(getByText("Fetched").parentElement!);
    expect(getByText(/including streaming, health checks/).getAttribute("aria-hidden")).toBe(
      "false",
    );
  });
});
