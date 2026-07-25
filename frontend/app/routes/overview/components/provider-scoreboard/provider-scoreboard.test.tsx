import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { OutageBuckets, Sparkline } from "./provider-scoreboard";

describe("OutageBuckets", () => {
    it("keeps a brief trip inside its single time bucket", () => {
        const values = Array.from({ length: 24 }, (_, index) => index === 10 ? 1 : 0);
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
        const retries = renderToStaticMarkup(<Sparkline values={[0, 0, 0]} tone="warning" eventsOnly />);

        expect(errors).toContain("var(--color-base-content)");
        expect(errors).not.toContain("var(--color-error)");
        expect(retries).toContain("var(--color-base-content)");
        expect(retries).not.toContain("var(--color-warning)");
    });

    it("colors only the portions surrounding event buckets", () => {
        const markup = renderToStaticMarkup(<Sparkline values={[0, 2, 0, 0, 4, 0]} tone="error" eventsOnly />);

        expect(markup.match(/<path/g)).toHaveLength(2);
        expect(markup).toContain("var(--color-base-content)");
        expect(markup).toContain("var(--color-error)");
    });
});
