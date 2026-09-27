// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";
import { cleanup, render, screen, fireEvent } from "@testing-library/react";
import { LibraryFileModal } from "./file-modal";
import type { LibraryCatalogItem } from "~/clients/backend-client.server";

vi.mock("~/components/ui", async (importOriginal) => {
  const actual = await importOriginal<typeof import("~/components/ui")>();
  return {
    ...actual,
    Modal: ({ open, title, children }: { open: boolean; title: string; children: ReactNode }) =>
      open ? (
        <div role="dialog" aria-label={title}>
          {children}
        </div>
      ) : null,
    Icon: () => null,
  };
});

vi.mock("~/components/media-preview", () => ({
  MediaPreview: ({ fileName, onClose }: { fileName: string; onClose: () => void }) => (
    <div>
      <span>preview:{fileName}</span>
      <button onClick={onClose}>close-preview</button>
    </div>
  ),
}));

const item: LibraryCatalogItem = {
  kind: "internal",
  davItemId: "11111111-1111-1111-1111-111111111111",
  displayName: "detail-film.mkv",
  contentPath: "/content/detail-film.mkv",
  size: 2048,
  mappingCount: 1,
  health: "healthy",
  mappings: [
    {
      linkPath: "movies/detail-film.mkv",
      targetText: "/mnt/.ids/x.mkv",
      mappingType: "internal",
      status: "valid",
    },
  ],
};

const baseProps = {
  item,
  quality: "1080p" as const,
  cachePercentage: 75,
  details: null,
  detailsLoading: false,
  detailsError: null,
  previewUrl: "/view/content/detail-film.mkv?downloadKey=k",
  canPrewarm: true,
  actionState: "idle" as const,
  feedback: null,
  onClose: () => {},
  onPreview: () => {},
  onRunHealthCheck: () => {},
  onRequeue: () => {},
  onPrewarm: () => {},
};

describe("LibraryFileModal", () => {
  afterEach(cleanup);
  it("renders file facts, mappings, and empty health state", () => {
    render(<LibraryFileModal {...baseProps} />);
    expect(screen.getByRole("dialog", { name: "detail-film.mkv" })).toBeTruthy();
    expect(screen.getByText(/movies\/detail-film\.mkv/)).toBeTruthy();
    expect(screen.getByText("75%")).toBeTruthy();
    expect(screen.getByText(/no health checks recorded/i)).toBeTruthy();
  });

  it("opens preview and fires modal actions", () => {
    const onPreview = vi.fn();
    const onRequeue = vi.fn();
    render(<LibraryFileModal {...baseProps} onPreview={onPreview} onRequeue={onRequeue} />);
    fireEvent.click(screen.getByRole("button", { name: /preview/i }));
    fireEvent.click(screen.getByRole("button", { name: /requeue/i }));
    expect(onPreview).toHaveBeenCalledTimes(1);
    expect(onRequeue).toHaveBeenCalledTimes(1);
  });

  it("hides prewarm when native cache is inactive", () => {
    render(<LibraryFileModal {...baseProps} canPrewarm={false} />);
    expect(screen.queryByRole("button", { name: /prewarm/i })).toBeNull();
    expect(screen.getByText(/native cache is inactive/i)).toBeTruthy();
  });
});
