import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { LiveReadsPanel } from "./live-reads-panel";

describe("LiveReadsPanel", () => {
    it("keeps the dashboard rail visible when there are no active reads", () => {
        const markup = renderToStaticMarkup(<LiveReadsPanel />);

        expect(markup).toContain("Right now");
        expect(markup).toContain("No active reads.");
    });
});
