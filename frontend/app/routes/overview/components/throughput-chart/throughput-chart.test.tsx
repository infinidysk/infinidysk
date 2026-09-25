// @vitest-environment jsdom
import { cleanup, fireEvent, render } from "@testing-library/react";
import { renderToStaticMarkup } from "react-dom/server";
import { afterEach, describe, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import type { ThroughputPoint } from "~/clients/backend-client.server";
import { formatBytes } from "../../utils/format";
import { ThroughputChart, type ThroughputChartProps } from "./throughput-chart";

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

function chartProps(points: ThroughputPoint[]): ThroughputChartProps {
  return {
    points,
    totalArticles: points.reduce((sum, item) => sum + item.articles, 0),
    totalClientArticles: points.reduce((sum, item) => sum + item.clientArticles, 0),
    totalQueueArticles: points.reduce((sum, item) => sum + item.queueArticles, 0),
    totalErrors: points.reduce((sum, item) => sum + item.errors, 0),
    totalMisses: 0,
    totalBytesServed: 0,
    totalBytesFetched: 0,
    bucketSizeMs: 60000,
    window: "24h",
  };
}

describe("ThroughputChart", () => {
  afterEach(() => {
    cleanup();
  });
  it("previews without rescaling, isolates small imports, and restores all series", () => {
    const { container, getByRole, getByText } = render(
      <ThroughputChart
        points={[
          { ...point(0), bucket: 0 },
          { ...point(10012, 10, 0, 2), bucket: 60000 },
        ]}
        totalArticles={10012}
        totalClientArticles={10}
        totalQueueArticles={2}
        totalMisses={0}
        totalErrors={0}
        totalBytesServed={100}
        totalBytesFetched={200}
        bucketSizeMs={60000}
        window="24h"
      />,
    );
    const imports = getByRole("button", { name: "Imports · 2" });
    const originalPath = container
      .querySelector('[data-series="queue-articles"]')
      ?.getAttribute("d");
    fireEvent.mouseEnter(imports);
    expect(container.querySelector('[data-series="queue-articles"]')?.getAttribute("d")).toBe(
      originalPath,
    );
    expect(
      container.querySelector('[data-series="client-articles"]')?.getAttribute("style"),
    ).toContain("opacity: 0.3");
    fireEvent.click(imports);
    expect(imports.getAttribute("aria-pressed")).toBe("true");
    expect(container.querySelectorAll("[data-series]")).toHaveLength(1);
    expect(container.querySelector('[data-series="queue-articles"]')?.getAttribute("d")).toContain(
      "800.0,6.0",
    );
    expect(getByText("Peak import attempts 2 / min")).toBeTruthy();
    expect(getByText("Successful reads").parentElement?.textContent).toContain("10,012");
    fireEvent.mouseEnter(getByRole("button", { name: "WebDAV Clients · 10" }));
    expect(container.querySelectorAll("[data-series]")).toHaveLength(1);
    expect(getByText("Peak import attempts 2 / min")).toBeTruthy();
    fireEvent.click(imports);
    expect(imports.getAttribute("aria-pressed")).toBe("false");
    expect(container.querySelectorAll("[data-series]")).toHaveLength(3);
    expect(container.querySelector('[data-series="queue-articles"]')?.getAttribute("d")).toBe(
      originalPath,
    );
  });
  it("does not draw the green series when every article bucket is zero", () => {
    const markup = renderMarkup([point(0, 0, 1), point(0)], 1);

    expect(markup).not.toContain('data-series="client-articles"');
    expect(markup).not.toContain('data-series="queue-articles"');
    expect(markup).not.toContain('data-series="app-articles"');
    expect(markup).toContain('data-series="errors"');
  });

  it("reveals bucket details on Tab focus and supports navigation and dismissal", async () => {
    const user = userEvent.setup();
    const props = chartProps(
      [0, 3, 8].map((value, index) => ({ ...point(value, value), bucket: index * 60000 })),
    );
    const { container, getByRole, getByText } = render(<ThroughputChart {...props} />);
    const chart = getByRole("img");
    const status = container.querySelector("#overview-throughput-keyboard-status");
    await user.click(getByText("Fetched").parentElement!);
    await user.tab();
    expect(document.activeElement).toBe(chart);
    expect(status?.textContent).toContain("8 client attempts");
    await user.keyboard("{Home}");
    expect(status?.textContent).toContain("0 client attempts");
    await user.keyboard("{ArrowRight}");
    expect(status?.textContent).toContain("3 client attempts");
    await user.keyboard("{End}");
    expect(status?.textContent).toContain("8 client attempts");
    await user.keyboard("{Escape}");
    expect(status?.textContent).toBe("");
    expect(container.querySelector("[data-marker-series]")).toBeNull();
    await user.keyboard("{ArrowLeft}");
    expect(status?.textContent).toContain("8 client attempts");
    await user.tab();
    const client = getByRole("button", { name: "WebDAV Clients · 11" });
    expect(document.activeElement).toBe(client);
    expect(status?.textContent).toBe("");
    await user.keyboard("{Enter}");
    expect(client.getAttribute("aria-pressed")).toBe("true");
    await user.keyboard(" ");
    expect(client.getAttribute("aria-pressed")).toBe("false");
  });

  it("selects the clicked bucket and hands off between pointer, touch, and keyboard", () => {
    const props = chartProps(
      [2, 4, 6].map((value, index) => ({ ...point(value, value), bucket: index * 60000 })),
    );
    const { container, getByRole } = render(<ThroughputChart {...props} />);
    const chart = getByRole("img");
    vi.spyOn(chart, "getBoundingClientRect").mockReturnValue(new DOMRect(0, 0, 800, 160));
    fireEvent.mouseMove(chart, { clientX: 800 });
    expect(document.activeElement).not.toBe(chart);
    fireEvent.click(chart, { clientX: 400 });
    expect(document.activeElement).toBe(chart);
    const status = container.querySelector("#overview-throughput-keyboard-status");
    expect(status?.textContent).toContain("4 client attempts");
    fireEvent.keyDown(chart, { key: "ArrowLeft" });
    expect(status?.textContent).toContain("2 client attempts");
    fireEvent.touchStart(chart, { touches: [{ clientX: 800 }] });
    expect(
      container.querySelector('[data-marker-series="client-articles"]')?.getAttribute("style"),
    ).toContain("left: 100%");
    fireEvent.keyDown(chart, { key: "ArrowRight" });
    expect(status?.textContent).toContain("4 client attempts");
    expect(
      container.querySelector('[data-marker-series="client-articles"]')?.getAttribute("style"),
    ).toContain("left: 50%");
  });

  it("shades only the emphasized series, closes separate runs, and draws its stroke last", () => {
    const props = chartProps(
      [0, 5, 0, 0, 3, 0].map((value, index) => ({
        ...point(value + 1, value),
        bucket: index * 60000,
      })),
    );
    const { container, getByRole } = render(<ThroughputChart {...props} />);
    const client = getByRole("button", { name: "WebDAV Clients · 8" });
    expect(container.querySelector("[data-area-series]")).toBeNull();
    fireEvent.mouseEnter(client);
    const area = container.querySelector('[data-area-series="client-articles"]');
    expect(container.querySelectorAll("[data-area-series]")).toHaveLength(1);
    expect(area?.getAttribute("fill")).toBe("var(--color-success)");
    expect(area?.getAttribute("d")?.match(/M/g)).toHaveLength(2);
    expect(area?.getAttribute("d")?.match(/Z/g)).toHaveLength(2);
    expect(area?.getAttribute("d")).toContain("L320.0,156.0 Z");
    expect(container.querySelector("svg")?.lastElementChild?.getAttribute("data-series")).toBe(
      "client-articles",
    );
    fireEvent.mouseLeave(client);
    expect(container.querySelector("[data-area-series]")).toBeNull();
    fireEvent.focus(client);
    expect(container.querySelector("[data-area-series]")).not.toBeNull();
    fireEvent.blur(client);
    expect(container.querySelector("[data-area-series]")).toBeNull();
  });

  it("matches cursor colors and positions to visible series, including a centered singleton", () => {
    const { container, getByRole } = render(
      <ThroughputChart {...chartProps([point(10012, 10, 3, 2)])} />,
    );
    fireEvent.focus(getByRole("img"));
    expect(container.querySelectorAll("[data-marker-series]")).toHaveLength(4);
    expect(
      container.querySelector('[data-marker-series="app-articles"]')?.getAttribute("style"),
    ).toContain("var(--color-info)");
    expect(container.querySelector('[data-series="client-articles"]')?.getAttribute("d")).toMatch(
      /^M400\.0,/,
    );
    expect(container.querySelector('[data-series="errors"]')?.getAttribute("d")).toMatch(
      /^M400\.0,/,
    );
    fireEvent.click(getByRole("button", { name: "Imports · 2" }));
    fireEvent.focus(getByRole("img"));
    expect(container.querySelectorAll("[data-marker-series]")).toHaveLength(1);
    expect(
      container.querySelector('[data-marker-series="queue-articles"]')?.getAttribute("style"),
    ).toContain("var(--color-secondary)");
    expect(
      container.querySelector('[data-marker-series="queue-articles"]')?.getAttribute("style"),
    ).toContain("left: 50%; top: 3.75%");
    expect(container.querySelector(".tooltip-open")?.getAttribute("style")).toContain(
      "--cursor-y: 3.75%",
    );
    expect(container.querySelector("svg")?.getAttribute("aria-hidden")).toBe("true");
  });

  it("handles empty isolated series and switches directly to error scaling", () => {
    const { container, getByRole, getByText } = render(
      <ThroughputChart {...chartProps([point(3, 3, 10)])} />,
    );
    fireEvent.click(getByRole("button", { name: "Imports · 0" }));
    expect(container.querySelectorAll("[data-series]")).toHaveLength(0);
    expect(container.querySelector("[data-area-series]")).toBeNull();
    expect(getByText("Peak import attempts 0 / min")).toBeTruthy();
    expect(getByText("1")).toBeTruthy();
    fireEvent.click(getByRole("button", { name: "Errors" }));
    expect(container.querySelectorAll("[data-series]")).toHaveLength(1);
    expect(container.querySelector('[data-series="errors"]')?.getAttribute("d")).toContain(
      "400.0,6.0",
    );
    expect(getByText("Peak errors 10 / min")).toBeTruthy();
    expect(getByRole("button", { name: "Imports · 0" }).getAttribute("aria-pressed")).toBe("false");
  });

  it("preserves isolation through polling and resets it when the time window changes", () => {
    const props = chartProps([{ ...point(12, 0, 0, 2), bucket: 60000 }]);
    const { container, getByRole, getByText, rerender } = render(<ThroughputChart {...props} />);
    fireEvent.click(getByRole("button", { name: "Imports · 2" }));
    const updated = chartProps([{ ...point(23, 0, 0, 3), bucket: 0 }, ...props.points]);
    rerender(<ThroughputChart {...updated} />);
    expect(getByRole("button", { name: "Imports · 5" }).getAttribute("aria-pressed")).toBe("true");
    expect(getByText("Peak import attempts 3 / min")).toBeTruthy();
    expect(container.querySelectorAll("[data-series]")).toHaveLength(1);
    rerender(<ThroughputChart {...updated} window="1h" />);
    expect(getByRole("button", { name: "Imports · 5" }).getAttribute("aria-pressed")).toBe("false");
    expect(container.querySelectorAll("[data-series]")).toHaveLength(2);
    expect(container.querySelector("[data-area-series]")).toBeNull();
  });

  it("keeps the isolated errors control available when polling clears errors", () => {
    const props = chartProps([point(3, 0, 2)]);
    const { container, getByRole, rerender } = render(<ThroughputChart {...props} />);
    fireEvent.click(getByRole("button", { name: "Errors" }));
    rerender(<ThroughputChart {...chartProps([point(3, 3)])} />);
    const errorsButton = getByRole("button", { name: "Errors" });
    expect(errorsButton.getAttribute("aria-pressed")).toBe("true");
    fireEvent.click(errorsButton);
    expect(container.querySelector('button[aria-pressed="true"]')).toBeNull();
    expect(container.querySelector('[data-series="client-articles"]')).toBeTruthy();
  });

  it("draws the green series when an article bucket has activity", () => {
    const markup = renderMarkup([point(0), point(2, 2)]);

    expect(markup).toContain('data-series="client-articles"');
  });

  it("uses a solid blue swatch for maintenance reads in the legend", () => {
    const markup = renderMarkup([point(3, 1)]);

    expect(markup).toContain("Maintenance · 2");
    expect(markup).toContain("border-t-2 border-info");
    expect(markup).not.toContain("border-dashed");
  });

  it("splits import attempts into a violet series distinct from client and maintenance", () => {
    // 10 attempts: 3 client, 5 import, 2 residual maintenance.
    const markup = renderMarkup([point(0), point(10, 3, 0, 5)]);

    expect(markup).toContain('data-series="client-articles"');
    expect(markup).toContain('data-series="queue-articles"');
    expect(markup).toContain('data-series="app-articles"');
    expect(markup).toContain("WebDAV Clients · 3");
    expect(markup).toContain("Imports · 5");
    expect(markup).toContain("Maintenance · 2");
    expect(markup).toContain("bg-secondary");
    expect(markup).toContain("3 client attempts, 5 import attempts, 2 maintenance attempts");
  });

  it("treats buckets recorded before import tracking as maintenance and clamps overlaps", () => {
    // Legacy bucket: queueArticles missing/zero → everything non-client stays blue.
    const legacy = renderMarkup([point(4, 1)]);
    expect(legacy).not.toContain('data-series="queue-articles"');
    expect(legacy).toContain("Imports · 0");
    expect(legacy).toContain("Maintenance · 3");

    // Over-reported import count is clamped to what is left after client attempts.
    const clamped = renderMarkup([point(4, 3, 0, 9)]);
    expect(clamped).toContain("Imports · 1");
    expect(clamped).toContain("Maintenance · 0");
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

    expect(markup).toContain("WebDAV Clients · 10");
    expect(markup).toContain("Peak download");
    expect(markup).not.toContain("bg-base-content/40");
    expect(markup).not.toContain("Maintenance · 0 · peak");
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
