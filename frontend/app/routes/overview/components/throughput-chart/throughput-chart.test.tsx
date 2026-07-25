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
            window="24h" />,
    );
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
});
