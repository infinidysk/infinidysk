// @vitest-environment jsdom
import { cleanup, render as renderDom, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderToStaticMarkup } from "react-dom/server";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { HealthCheckResult } from "~/clients/backend-client.server";
import {
  HealthAttentionTable,
  HealthHistoryTable,
  type HealthHistoryFilter,
} from "./health-history-table";

function table(items: HealthCheckResult[] = [], filter: HealthHistoryFilter = "all") {
  return (
    <HealthHistoryTable
      items={items}
      totalCount={items.length}
      page={1}
      pageSize={25}
      pageSizeOptions={[25, 50]}
      filter={filter}
      refreshing={false}
      onFilterSelected={vi.fn()}
      onPageSelected={vi.fn()}
      onPageSizeSelected={vi.fn()}
      onRefresh={vi.fn()}
    />
  );
}

function render(items: HealthCheckResult[] = [], filter: HealthHistoryFilter = "all") {
  return renderToStaticMarkup(table(items, filter));
}

function attentionTable(
  options: {
    items?: HealthCheckResult[];
    canRequeueActionNeeded?: boolean;
    requeueingActionNeeded?: boolean;
    onRequeueActionNeeded?: () => void;
    onDelete?: (item: HealthCheckResult) => void;
  } = {},
) {
  return (
    <HealthAttentionTable
      items={options.items ?? []}
      totalCount={2}
      page={1}
      pageSize={25}
      pageSizeOptions={[25, 50]}
      refreshing={false}
      onPageSelected={vi.fn()}
      onPageSizeSelected={vi.fn()}
      onRefresh={vi.fn()}
      canRequeueActionNeeded={options.canRequeueActionNeeded ?? false}
      requeueingActionNeeded={options.requeueingActionNeeded ?? false}
      onRequeueActionNeeded={options.onRequeueActionNeeded ?? vi.fn()}
      onDelete={options.onDelete}
    />
  );
}

afterEach(cleanup);

describe("HealthHistoryTable", () => {
  it("combines status and reason only in the attention table", () => {
    const item: HealthCheckResult = {
      id: "result-1",
      davItemId: "file-1",
      path: "/content/example.mkv",
      nzbFileName: null,
      jobName: null,
      createdAt: "2026-09-18T00:00:00Z",
      result: 1,
      repairStatus: 3,
      message: "Missing library link",
    };
    const { unmount } = renderDom(attentionTable({ items: [item] }));
    expect(screen.getAllByRole("columnheader").map((header) => header.textContent)).toEqual([
      "NZB",
      "Status & reason",
      "Last checked",
    ]);
    const cells = screen.getAllByRole("cell");
    expect(cells).toHaveLength(3);
    expect(cells[1]?.textContent).toContain("Action needed");
    expect(cells[1]?.textContent).toContain("Missing library link");
    unmount();
    renderDom(table([item]));
    expect(screen.getAllByRole("columnheader").map((header) => header.textContent)).toEqual([
      "NZB",
      "Status",
      "Reason",
      "When",
    ]);
    expect(screen.getAllByRole("cell")).toHaveLength(4);
  });

  it("offers search and deletion independently of background re-checks", async () => {
    const item: HealthCheckResult = {
      id: "result-1",
      davItemId: "file-1",
      path: "/content/example.mkv",
      nzbFileName: "Example & More.nzb",
      jobName: null,
      createdAt: "2026-09-18T00:00:00Z",
      result: 1,
      repairStatus: 3,
      message: "Missing link",
    };
    const onDelete = vi.fn();
    const { unmount } = renderDom(attentionTable({ items: [item], onDelete }));
    expect(
      screen.getAllByRole("link", { name: "Search indexers for Example & More.nzb" })[0],
    ).toHaveProperty("href", expect.stringContaining("/search?q=Example+%26+More"));
    await userEvent
      .setup()
      .click(screen.getAllByRole("button", { name: "Delete Example & More.nzb" })[0]!);
    expect(onDelete).toHaveBeenCalledWith(item);
    expect(screen.queryByRole("button", { name: "Re-check Example & More.nzb" })).toBeNull();
    unmount();
    renderDom(attentionTable({ items: [item] }));
    expect(screen.queryByRole("button", { name: "Delete Example & More.nzb" })).toBeNull();
    expect(screen.queryByRole("link")).toBeNull();
  });

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

    expect(markup).toContain("No deleted or repaired items");
    expect(markup).toContain("health-check retention");
    expect(markup).not.toContain("Action needed");
    expect(markup).not.toContain("Re-check action needed");
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
    const markup = renderToStaticMarkup(
      attentionTable({
        items: [
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
      }),
    );

    expect(markup).toContain("Action needed");
    expect(markup).toContain("badge-warning");
    expect(markup).toContain("Streaming payload missing.");
    expect(markup).not.toContain("badge-info");
  });

  it("shows the bulk re-check action only when permitted", () => {
    const allowed = renderToStaticMarkup(attentionTable({ canRequeueActionNeeded: true }));
    const hidden = renderToStaticMarkup(attentionTable());

    expect(allowed).toContain("Re-check action needed");
    expect(hidden).not.toContain("Re-check action needed");
  });

  it("disables the bulk action while items are being queued", () => {
    const markup = renderToStaticMarkup(
      attentionTable({
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
      attentionTable({
        canRequeueActionNeeded: true,
        onRequeueActionNeeded,
      }),
    );

    await userEvent.setup().click(screen.getByRole("button", { name: "Re-check action needed" }));

    expect(onRequeueActionNeeded).toHaveBeenCalledOnce();
  });

  it("re-checks only the selected attention item", async () => {
    const onRequeueActionNeeded = vi.fn();
    renderDom(
      attentionTable({
        canRequeueActionNeeded: true,
        onRequeueActionNeeded,
        items: [
          {
            id: "result-1",
            davItemId: "file-1",
            path: "/content/example.mkv",
            nzbFileName: null,
            jobName: null,
            createdAt: "2026-09-18T00:00:00Z",
            result: 1,
            repairStatus: 3,
            message: "Provider unavailable",
          },
        ],
      }),
    );

    await userEvent
      .setup()
      .click(screen.getAllByRole("button", { name: "Re-check example.mkv" })[0]!);

    expect(onRequeueActionNeeded).toHaveBeenCalledWith("file-1");
  });
});
