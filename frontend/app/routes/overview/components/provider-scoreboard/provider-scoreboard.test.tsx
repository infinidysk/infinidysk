import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { OutageBuckets } from "./provider-scoreboard";

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
