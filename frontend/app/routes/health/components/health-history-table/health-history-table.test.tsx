// @vitest-environment jsdom
import { cleanup, render as renderDom, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderToStaticMarkup } from "react-dom/server";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { HealthCheckResult } from "~/clients/backend-client.server";
import { HealthHistoryTable, type HealthHistoryFilter } from "./health-history-table";

function table(
  items: HealthCheckResult[] = [],
  filter: HealthHistoryFilter = "all",
  options: {
    canRequeueActionNeeded?: boolean;
    requeueingActionNeeded?: boolean;
    onRequeueActionNeeded?: () => void;
  } = {},
) {
  return (
    <HealthHistoryTable
      items={items}
      totalCount={items.length}
      page={1}
      pageSize={25}
      pageSizeOptions={[25, 50]}
      filter={filter}
      refreshing={false}
      canRequeueActionNeeded={options.canRequeueActionNeeded ?? false}
      requeueingActionNeeded={options.requeueingActionNeeded ?? false}
      onFilterSelected={vi.fn()}
      onPageSelected={vi.fn()}
      onPageSizeSelected={vi.fn()}
      onRefresh={vi.fn()}
      onRequeueActionNeeded={options.onRequeueActionNeeded ?? vi.fn()}
    />
  );
}

function render(items: HealthCheckResult[] = [], filter: HealthHistoryFilter = "all") {
  return renderToStaticMarkup(table(items, filter));
}

afterEach(cleanup);

describe("HealthHistoryTable", () => {
  it("shows the snapped NZB identity and deleted disposition", () => {
    const markup = render([
      {
        id: "1",
        createdAt: "2026-08-17T12:00:00Z",
        davItemId: "dav-1",
        path: "/content/movies/Example/Example.mkv",
        nzbFileName: "Example.Release.nzb",
        jobName: "Example.Release",
        result: 1,
        repairStatus: 2,
        message: "Deleted file.",
      },
    ]);

    expect(markup).toContain("Example.Release.nzb");
    expect(markup).toContain("Example.Release");
    expect(markup).toContain("Deleted");
    expect(markup).toContain("Deleted file.");
  });

  it("falls back to the WebDAV path for legacy rows", () => {
    const markup = render([
      {
        id: "1",
        createdAt: "2026-08-17T12:00:00Z",
        davItemId: "dav-1",
        path: "/content/tv/Example/episode.mkv",
        nzbFileName: null,
        jobName: null,
        result: 1,
        repairStatus: 1,
        message: null,
      },
    ]);

    expect(markup).toContain("episode.mkv");
    expect(markup).toContain("/content/tv/Example/episode.mkv");
    expect(markup).toContain("Repaired");
  });

  it("explains empty repair history", () => {
    const markup = render();

    expect(markup).toContain("No deleted, repaired, or action needed items");
    expect(markup).toContain("health-check retention");
  });

  it("shows a warning-toned badge for degraded rows", () => {
    const markup = render([
      {
        id: "1",
        createdAt: "2026-08-17T12:00:00Z",
        davItemId: "dav-1",
        path: "/content/movies/Example/Example.mkv",
        nzbFileName: "Example.Release.nzb",
        jobName: "Example.Release",
        result: 2,
        repairStatus: 0,
        message: "1 missing segment(s) within tolerance.",
      },
    ]);

    expect(markup).toContain("Degraded");
    expect(markup).toContain("badge-warning");
    expect(markup).not.toContain("badge-info");
  });

  it("marks the degraded filter button active when selected", () => {
    const markup = render([], "degraded");

    expect(markup).toContain("No degraded items");
    expect(markup).toContain("focus-within:outline-primary");
    expect(markup).toMatch(
      /<label[^>]*btn-active[^>]*><input[^>]*checked=""[^>]*\/><span>Degraded<\/span><\/label>/,
    );
  });

  it("shows action-needed rows with a warning badge", () => {
    const markup = render(
      [
        {
          id: "1",
          createdAt: "2026-08-17T12:00:00Z",
          davItemId: "dav-1",
          path: "/content/tv/Example/episode.mkv",
          nzbFileName: "Example.Release.nzb",
          jobName: "Example.Release",
          result: 1,
          repairStatus: 3,
          message: "Streaming payload missing.",
        },
      ],
      "action-needed",
    );

    expect(markup).toContain("Action needed");
    expect(markup).toContain("badge-warning");
    expect(markup).toContain("Streaming payload missing.");
    expect(markup).not.toContain("badge-info");
  });

  it("shows the bulk re-check action only when permitted", () => {
    const allowed = renderToStaticMarkup(table([], "all", { canRequeueActionNeeded: true }));
    const hidden = renderToStaticMarkup(table());

    expect(allowed).toContain("Re-check action needed");
    expect(hidden).not.toContain("Re-check action needed");
  });

  it("disables the bulk action while items are being queued", () => {
    const markup = renderToStaticMarkup(
      table([], "all", {
        canRequeueActionNeeded: true,
        requeueingActionNeeded: true,
      }),
    );

    expect(markup).toContain("Queueing...");
    expect(markup).toMatch(/<button[^>]*disabled=""[^>]*>.*Queueing\.\.\.<\/button>/);
  });

  it("invokes the bulk re-check callback", async () => {
    const onRequeueActionNeeded = vi.fn();
    renderDom(
      table([], "all", {
        canRequeueActionNeeded: true,
        onRequeueActionNeeded,
      }),
    );

    await userEvent.setup().click(screen.getByRole("button", { name: "Re-check action needed" }));

    expect(onRequeueActionNeeded).toHaveBeenCalledOnce();
  });
});
