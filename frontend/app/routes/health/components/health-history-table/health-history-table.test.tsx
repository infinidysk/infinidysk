import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import type { HealthCheckResult } from "~/clients/backend-client.server";
import { HealthHistoryTable } from "./health-history-table";

function render(items: HealthCheckResult[] = []) {
    return renderToStaticMarkup(
        <HealthHistoryTable
            items={items}
            totalCount={items.length}
            page={1}
            pageSize={25}
            pageSizeOptions={[25, 50]}
            filter="all"
            refreshing={false}
            onFilterSelected={vi.fn()}
            onPageSelected={vi.fn()}
            onPageSizeSelected={vi.fn()}
            onRefresh={vi.fn()}
        />,
    );
}

describe("HealthHistoryTable", () => {
    it("shows the snapped NZB identity and deleted disposition", () => {
        const markup = render([{
            id: "1",
            createdAt: "2026-08-17T12:00:00Z",
            davItemId: "dav-1",
            path: "/content/movies/Example/Example.mkv",
            nzbFileName: "Example.Release.nzb",
            jobName: "Example.Release",
            result: 1,
            repairStatus: 2,
            message: "Deleted file.",
        }]);

        expect(markup).toContain("Example.Release.nzb");
        expect(markup).toContain("Example.Release");
        expect(markup).toContain("Deleted");
        expect(markup).toContain("Deleted file.");
    });

    it("falls back to the WebDAV path for legacy rows", () => {
        const markup = render([{
            id: "1",
            createdAt: "2026-08-17T12:00:00Z",
            davItemId: "dav-1",
            path: "/content/tv/Example/episode.mkv",
            nzbFileName: null,
            jobName: null,
            result: 1,
            repairStatus: 1,
            message: null,
        }]);

        expect(markup).toContain("episode.mkv");
        expect(markup).toContain("/content/tv/Example/episode.mkv");
        expect(markup).toContain("Repaired");
    });

    it("explains empty repair history", () => {
        const markup = render();

        expect(markup).toContain("No deleted or repaired items");
        expect(markup).toContain("health-check retention");
    });
});
