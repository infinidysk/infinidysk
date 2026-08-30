import { renderToStaticMarkup } from "react-dom/server";
import { MemoryRouter } from "react-router";
import { describe, expect, it } from "vitest";
import type { ProviderRow } from "~/clients/backend-client.server";
import { OutageBuckets, ProviderScoreboard, Sparkline } from "./provider-scoreboard";

const provider = (speedMbPerSec: number | null): ProviderRow => ({
  provider: "provider-1",
  articles: 10,
  bytesFetched: 1_000_000,
  errors: 0,
  retries: 0,
  speedMbPerSec,
  speedSpark: speedMbPerSec == null ? [] : [speedMbPerSec],
  avgDurationMs: 100,
  errorRate: 0,
  spark: [10],
});

describe("ProviderScoreboard", () => {
  it("shows sustained speed and an em dash when speed is unavailable", () => {
    const active = renderToStaticMarkup(
      <MemoryRouter>
        <ProviderScoreboard providers={[provider(12.34)]} window="1h" />
      </MemoryRouter>,
    );
    const idle = renderToStaticMarkup(
      <MemoryRouter>
        <ProviderScoreboard providers={[provider(null)]} window="1h" />
      </MemoryRouter>,
    );

    expect(active).toContain(">MB/s<");
    expect(active).toContain(">12.3<");
    expect(active).toContain("not wall-clock aggregate bandwidth");
    expect(active).toContain('aria-label="Article share: 100%"');
    expect(idle).toContain(">—<");
  });

  it("places the speed chart after the horizontal scroll wrapper", () => {
    const selected = {
      ...provider(2),
      speedSeries: [{ bucket: 1_700_000_000_000, speedMbPerSec: 2.25, bytesFetched: 4_000 }],
    };
    const markup = renderToStaticMarkup(
      <MemoryRouter>
        <ProviderScoreboard
          providers={[selected]}
          window="1h"
          selectedProvider="provider-1"
          onSelectProvider={() => {}}
        />
      </MemoryRouter>,
    );

    expect(markup).toContain('aria-expanded="true"');
    expect(markup).toContain('id="provider-speed-chart"');
    expect(markup.indexOf("overflow-x-auto")).toBeGreaterThan(-1);
    expect(markup.indexOf('id="provider-speed-chart"')).toBeGreaterThan(
      markup.indexOf("overflow-x-auto"),
    );
    expect(markup).toContain("2.25 MB/s");
    expect(markup).not.toContain("min-w-[800px]");
  });

  it("captions retained history when the series is truncated", () => {
    const markup = renderToStaticMarkup(
      <MemoryRouter>
        <ProviderScoreboard
          providers={[provider(1)]}
          window="all"
          selectedProvider="provider-1"
          onSelectProvider={() => {}}
          providerSpeedHistoryTruncated
        />
      </MemoryRouter>,
    );

    expect(markup).toContain("retained provider history (last 365 days)");
  });
});

describe("OutageBuckets", () => {
  it("keeps a brief trip inside its single time bucket", () => {
    const values = Array.from({ length: 24 }, (_, index) => (index === 10 ? 1 : 0));
    const markup = renderToStaticMarkup(<OutageBuckets values={values} />);

    expect(markup.match(/<rect/g)).toHaveLength(1);
    expect(markup).toContain('height="1.5"');
    expect(markup).toContain("1% circuit open during this interval");
  });

  it("uses the fixed percentage scale for sustained outages", () => {
    const markup = renderToStaticMarkup(<OutageBuckets values={[0, 50, 100, 0]} />);

    expect(markup.match(/<rect/g)).toHaveLength(2);
    expect(markup).toContain('height="9"');
    expect(markup).toContain('height="18"');
    expect(markup).toContain('aria-label="Circuit-open time by interval, peak 100%"');
  });
});

describe("Sparkline event mode", () => {
  it("uses only the neutral line when every bucket is zero", () => {
    const errors = renderToStaticMarkup(<Sparkline values={[0, 0, 0]} tone="error" eventsOnly />);
    const retries = renderToStaticMarkup(
      <Sparkline values={[0, 0, 0]} tone="warning" eventsOnly />,
    );

    expect(errors).toContain("var(--color-base-content)");
    expect(errors).not.toContain("var(--color-error)");
    expect(retries).toContain("var(--color-base-content)");
    expect(retries).not.toContain("var(--color-warning)");
  });

  it("colors only the portions surrounding event buckets", () => {
    const markup = renderToStaticMarkup(
      <Sparkline values={[0, 2, 0, 0, 4, 0]} tone="error" eventsOnly />,
    );

    expect(markup.match(/<path/g)).toHaveLength(2);
    expect(markup).toContain("var(--color-base-content)");
    expect(markup).toContain("var(--color-error)");
  });

  it("uses the secondary token for throughput sparklines", () => {
    const markup = renderToStaticMarkup(<Sparkline values={[1, 2, 3]} tone="secondary" />);

    expect(markup).toContain("var(--color-secondary)");
    expect(markup).not.toContain("var(--color-success)");
  });
});
