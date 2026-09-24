import { describe, expect, it, vi } from "vitest";
import {
  initialFilesState,
  filesReducer,
  applyFilesPage,
  flattenFilesTree,
  topLevelRemovalTargets,
  FilesReadCoordinator,
} from "./files-state";
import { makeFileRow, makeFilesPage } from "./files-fixtures";
describe("Files state", () => {
  it("doesNotReopenCollapsedBranchOnLateResponse", () => {
    const parent = makeFileRow({ key: "parent", isDirectory: true, path: "/content/tv" });
    const child = makeFileRow({ key: "child", parentId: parent.id, path: "/content/tv/video.mkv" });
    let state = initialFilesState("query", makeFilesPage([parent]));
    state = filesReducer(state, { type: "toggle", key: parent.key });
    const job = {
      branchKey: parent.key,
      parentPath: parent.path,
      offset: 0,
      generation: 0,
      requestId: 1,
      parameters: new URLSearchParams(),
    };
    state = filesReducer(state, { type: "request", job });
    state = filesReducer(state, { type: "toggle", key: parent.key });
    state = filesReducer(state, {
      type: "success",
      job,
      page: makeFilesPage([child], { parentPath: parent.path }),
      loadedAt: 1,
    });
    expect(flattenFilesTree(state).map((row) => row.key)).toEqual([parent.key]);
    expect(state.branches[parent.key]?.keys).toEqual([child.key]);
  });
  it("pagesOneBranchWithoutReplacingSiblings", () => {
    const parent = makeFileRow({ key: "parent", isDirectory: true });
    const sibling = makeFileRow({ key: "sibling", id: "10000000-0000-0000-0000-000000000002" });
    let state = initialFilesState("query", makeFilesPage([parent, sibling]));
    const root = state.branches["root"];
    const job = {
      branchKey: parent.key,
      parentPath: parent.path,
      offset: 100,
      generation: 0,
      requestId: 1,
      parameters: new URLSearchParams(),
    };
    state = filesReducer(state, { type: "request", job });
    state = filesReducer(state, {
      type: "success",
      job,
      page: makeFilesPage([], { parentPath: parent.path, offset: 100 }),
      loadedAt: 1,
    });
    expect(state.branches["root"]).toBe(root);
    expect(state.rows[sibling.key]).toEqual(sibling);
  });
  it("prunesRemovedParentsAndStalePages", () => {
    const parent = makeFileRow({ key: "parent", isDirectory: true, path: "/content/tv" });
    const child = makeFileRow({ key: "child", path: "/content/tv/video.mkv" });
    let state = initialFilesState("query", makeFilesPage([parent]));
    const job = {
      branchKey: parent.key,
      parentPath: parent.path,
      offset: 0,
      generation: 0,
      requestId: 1,
      parameters: new URLSearchParams(),
    };
    state = filesReducer(state, { type: "request", job });
    state = filesReducer(state, {
      type: "success",
      job,
      page: makeFilesPage([child], { parentPath: parent.path }),
      loadedAt: 1,
    });
    state = filesReducer(state, { type: "toggle", key: parent.key });
    state = filesReducer(state, { type: "focus", key: child.key });
    state = filesReducer(state, { type: "removed", targets: [parent] });
    expect(state.branches[parent.key]).toBeUndefined();
    expect(state.rows[child.key]).toBeUndefined();
    expect(state.expanded.has(parent.key)).toBe(false);
    expect(state.focusedKey).toBeNull();
  });
  it("doesNotOverwriteNewerSharedRowMetadataOrCounts", () => {
    const row = makeFileRow({ health: "healthy" });
    let state = initialFilesState("query", makeFilesPage([row]));
    const job = {
      branchKey: "list",
      parentPath: "/content",
      offset: 0,
      generation: 0,
      requestId: 1,
      parameters: new URLSearchParams(),
    };
    state = filesReducer(state, { type: "request", job });
    state = filesReducer(state, {
      type: "success",
      job,
      page: makeFilesPage([makeFileRow({ health: "degraded" })], {
        mode: "list",
        observedAt: "2026-09-22T00:00:00Z",
        matchingFileCount: 55,
      }),
      loadedAt: 1,
    });
    expect(state.rows[row.key]?.health).toBe("healthy");
    expect(state.matchingFileCount).toBe(1);
  });
  it("keepsSameNamedFilesIndependentById", () => {
    const first = makeFileRow();
    const second = makeFileRow({ key: "other", id: "10000000-0000-0000-0000-000000000002" });
    const state = filesReducer(initialFilesState("query", makeFilesPage([first, second])), {
      type: "select",
      key: first.key,
    });
    expect(state.selected).toEqual(new Set([first.key]));
    expect(Object.keys(state.rows)).toHaveLength(2);
  });
  it("dropsOldGenerationAndOldBranchResponses", () => {
    const page = makeFilesPage([makeFileRow()]);
    const state = initialFilesState("query", page);
    expect(applyFilesPage(state, "root", 1, 0, 0, page, 0)).toBe(state);
    expect(applyFilesPage(state, "root", 0, 1, 0, page, 0)).toBe(state);
    expect(applyFilesPage(state, "root", 0, 0, 100, page, 0)).toBe(state);
  });
  it("expandsAndCollapsesOnlyOneBranch", () => {
    const parent = makeFileRow({ isDirectory: true });
    const state = initialFilesState("query", makeFilesPage([parent]));
    const expanded = filesReducer(state, { type: "toggle", key: parent.key });
    expect(state.expanded.size).toBe(0);
    expect(expanded.expanded.has(parent.key)).toBe(true);
    expect(
      flattenFilesTree(filesReducer(expanded, { type: "toggle", key: parent.key })),
    ).toHaveLength(1);
  });
  it("preservesExpansionAcrossFilterAndModeChanges", () => {
    const state = initialFilesState("query", makeFilesPage([]));
    state.expanded.add("parent");
    expect(
      filesReducer(state, {
        type: "reset",
        queryKey: "next",
        generation: 1,
        scopeChanged: false,
      }).expanded.has("parent"),
    ).toBe(true);
    expect(
      filesReducer(state, { type: "reset", queryKey: "next", generation: 1, scopeChanged: true })
        .expanded.size,
    ).toBe(0);
  });
  it("normalizesOverlappingRemovalTargets", () => {
    const parent = makeFileRow({ key: "parent", path: "/content/tv", isDirectory: true });
    const child = makeFileRow({ path: "/content/tv/movie.mkv" });
    expect(topLevelRemovalTargets([child, parent, child])).toEqual([parent]);
  });
  it("limitsConcurrentReadsToFourAndReleasesSlotsAfterAbort", async () => {
    const resolvers: Array<() => void> = [];
    let active = 0;
    let peak = 0;
    const coordinator = new FilesReadCoordinator(
      async (_, signal) => {
        active++;
        peak = Math.max(active, peak);
        try {
          await new Promise<void>((resolve, reject) => {
            resolvers.push(resolve);
            signal.addEventListener("abort", () => reject(new Error("aborted")));
          });
          return makeFilesPage([]);
        } finally {
          active--;
        }
      },
      { started: vi.fn(), completed: vi.fn(), failed: vi.fn() },
    );
    for (let index = 0; index < 10; index++)
      coordinator.enqueue(String(index), "/content", 0, new URLSearchParams());
    expect(active).toBe(4);
    coordinator.beginGeneration();
    await vi.waitFor(() => expect(active).toBe(0));
    expect(peak).toBe(4);
    coordinator.dispose();
  });
});
