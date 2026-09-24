// @vitest-environment jsdom
import { StrictMode } from "react";
import { MemoryRouter } from "react-router";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { defaultFilesFilters } from "~/clients/files-contract";
import { makeFileRow, makeFilesPage } from "~/clients/files-fixtures";
const mocks = vi.hoisted(() => ({
  readonly: vi.fn(),
  fetch: vi.fn<(input: string, options?: RequestInit) => Promise<Response>>(),
  playerMount: vi.fn(),
}));
vi.mock("~/auth/authorization", () => ({ useIsReadOnly: mocks.readonly }));
vi.mock("~/utils/shared-websocket", () => ({ useWebsocketTopics: vi.fn() }));
vi.mock("./media-preview/media-preview", async () => {
  const { useEffect } = await import("react");
  return {
    MediaPreview: ({ fileName, onClose }: { fileName: string; onClose: () => void }) => {
      useEffect(() => {
        mocks.playerMount();
      }, []);
      return (
        <div data-testid="player">
          {fileName}
          <button onClick={onClose}>Close player</button>
        </div>
      );
    },
  };
});
import { FilesBrowser } from "./files-browser";
const row = makeFileRow({
  health: "degraded",
  scanState: "queued",
  canSearchArr: true,
  searchArrDisabledReason: null,
});
const page = makeFilesPage([row]);
beforeEach(() => {
  HTMLDialogElement.prototype.showModal = function () {
    this.open = true;
  };
  HTMLDialogElement.prototype.close = function () {
    this.open = false;
  };
  vi.clearAllMocks();
  mocks.readonly.mockReturnValue(false);
  mocks.fetch.mockImplementation(async (input: string, options?: RequestInit) => {
    await Promise.resolve();
    const url = new URL(input, "http://localhost");
    if (options?.method === "POST")
      return Response.json({
        ok: true,
        result: { status: true, davItemId: row.id, state: "queued" },
      });
    if (url.searchParams.get("operation") === "delete-preview")
      return Response.json({
        status: true,
        fileCount: 1,
        dirCount: 0,
        totalBytes: 1024,
        linkedHistoryCount: 0,
      });
    return Response.json({
      ...page,
      mode: url.searchParams.get("mode") ?? "tree",
      parentPath: url.searchParams.get("parentPath") ?? "/content",
    });
  });
  vi.stubGlobal("fetch", mocks.fetch);
});
afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});
function mount(strict = false, initialPage = page) {
  const browser = (
    <MemoryRouter>
      <FilesBrowser
        scopePath="/content"
        initialPage={initialPage}
        initialFilters={defaultFilesFilters}
        initialMode="tree"
        initialSort="name"
        initialDirection="asc"
      />
    </MemoryRouter>
  );
  return render(strict ? <StrictMode>{browser}</StrictMode> : browser);
}
describe("Files browser", () => {
  it("filtersUnloadedBranchesViaResourceQuery", async () => {
    const directory = makeFileRow({
      key: "10000000-0000-0000-0000-000000000002",
      id: "10000000-0000-0000-0000-000000000002",
      name: "Unopened",
      path: "/content/Unopened",
      isDirectory: true,
      hasChildren: true,
    });
    mocks.fetch.mockResolvedValue(Response.json(makeFilesPage([directory])));
    mount(false, makeFilesPage([directory]));
    await screen.findByRole("button", { name: "Expand Unopened" });
    await userEvent.click(screen.getByText("Health", { selector: "summary" }));
    await userEvent.click(screen.getByRole("checkbox", { name: "Degraded" }));
    await waitFor(() =>
      expect(
        mocks.fetch.mock.calls.some(
          ([url]) =>
            String(url).includes("health=degraded") && String(url).includes("scopePath=%2Fcontent"),
        ),
      ).toBe(true),
    );
    expect(
      mocks.fetch.mock.calls.every(
        ([url]) => !String(url).includes("parentPath=%2Fcontent%2FUnopened"),
      ),
    ).toBe(true);
  });
  it("showsUnknownMembershipOnFailedScan", async () => {
    const failedPage = makeFilesPage([makeFileRow({ libraryState: "unknown" })], {
      libraryScanState: "unknown",
      libraryError: "Library scan unavailable.",
    });
    mocks.fetch.mockImplementation(() =>
      Promise.resolve(Response.json(failedPage)),
    );
    mount(false, failedPage);
    await screen.findByText("Library scan unavailable.");
    expect(screen.getByText("Library: Unknown")).toBeTruthy();
    expect(screen.queryByRole("gridcell", { name: "Not in library" })).toBeNull();
  });
  it("arrConfirmationNeverInvokesDelete", async () => {
    const read = mocks.fetch.getMockImplementation()!;
    const intents: string[] = [];
    mocks.fetch.mockImplementation((input: string, options?: RequestInit) => {
      if (options?.method !== "POST") return read(input, options);
      intents.push((options.body as FormData).get("intent") as string);
      return Promise.resolve(
        Response.json({
          ok: true,
          result: {
            status: true,
            davItemId: row.id,
            outcome: "unconfirmed",
            results: [
              {
                appType: "radarr",
                instanceName: "Synthetic",
                mediaIds: [1],
                commandId: null,
                state: "unconfirmed",
                error: "Check Arr before trying again.",
              },
            ],
          },
        }),
      );
    });
    mount();
    await userEvent.click(
      await screen.findByRole("button", { name: "Search in Arr Synthetic.mkv" }),
    );
    expect(intents).toEqual([]);
    await userEvent.click(screen.getByRole("button", { name: "Search in Arr" }));
    await screen.findByText(/Arr: unconfirmed/);
    expect(intents).toEqual(["arr-search"]);
  });
  it("partialDeleteRetainsOnlyFailedSelections", async () => {
    const second = makeFileRow({
      key: "10000000-0000-0000-0000-000000000002",
      id: "10000000-0000-0000-0000-000000000002",
      name: "Second.mkv",
      path: "/content/Second.mkv",
    });
    let remaining = [row, second];
    const removed: string[] = [];
    mocks.fetch.mockImplementation((input: string, options?: RequestInit) => {
      if (options?.method === "POST") {
        const id = (options.body as FormData).get("expectedDavItemId") as string;
        removed.push(id);
        if (id === second.id)
          return Promise.resolve(
            Response.json({ ok: false, error: "Synthetic conflict." }, { status: 409 }),
          );
        remaining = remaining.filter((item) => item.id !== id);
        return Promise.resolve(Response.json({ ok: true, result: { status: true } }));
      }
      if (String(input).includes("delete-preview"))
        return Promise.resolve(
          Response.json({
            status: true,
            fileCount: 1,
            dirCount: 0,
            totalBytes: 1024,
            linkedHistoryCount: 0,
          }),
        );
      return Promise.resolve(Response.json(makeFilesPage(remaining)));
    });
    mount(false, makeFilesPage(remaining));
    await screen.findByRole("checkbox", { name: "Select Second.mkv" });
    await userEvent.click(screen.getByRole("checkbox", { name: "Select Synthetic.mkv" }));
    await userEvent.click(screen.getByRole("checkbox", { name: "Select Second.mkv" }));
    await userEvent.click(screen.getByRole("button", { name: "Remove selected" }));
    await waitFor(() =>
      expect(screen.getByRole("button", { name: "Remove" }).hasAttribute("disabled")).toBe(false),
    );
    await userEvent.click(screen.getByRole("button", { name: "Remove" }));
    await screen.findByText("1 selected");
    expect(removed.filter((id) => id === row.id)).toHaveLength(1);
    expect(removed.filter((id) => id === second.id)).toHaveLength(1);
    expect(
      screen.getByRole<HTMLInputElement>("checkbox", { name: "Select Second.mkv" }).checked,
    ).toBe(true);
  });
  it("closesPlayerWhenItsFileIsRemoved", async () => {
    let deleted = false;
    const read = mocks.fetch.getMockImplementation()!;
    mocks.fetch.mockImplementation((input: string, options?: RequestInit) => {
      if (options?.method === "POST") {
        deleted = true;
        return Promise.resolve(Response.json({ ok: true, result: { status: true } }));
      }
      if (deleted && !String(input).includes("delete-preview"))
        return Promise.resolve(Response.json(makeFilesPage([])));
      return read(input, options);
    });
    mount();
    await userEvent.click(await screen.findByRole("button", { name: "Play Synthetic.mkv" }));
    await userEvent.click(screen.getByRole("button", { name: "Remove Synthetic.mkv" }));
    await waitFor(() =>
      expect(screen.getByRole("button", { name: "Remove" }).hasAttribute("disabled")).toBe(false),
    );
    await userEvent.click(screen.getByRole("button", { name: "Remove" }));
    await waitFor(() => expect(screen.queryByTestId("player")).toBeNull());
  });
  it("refreshesOnlyVisibleBranchesAndPausesWhenHidden", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    mount();
    await screen.findByRole("button", { name: "Play Synthetic.mkv" });
    const visibility = vi.spyOn(document, "visibilityState", "get").mockReturnValue("hidden");
    const count = mocks.fetch.mock.calls.length;
    await act(() => vi.advanceTimersByTimeAsync(60000));
    expect(mocks.fetch.mock.calls.length).toBe(count);
    visibility.mockReturnValue("visible");
    await act(async () => {
      document.dispatchEvent(new Event("visibilitychange"));
      await Promise.resolve();
    });
    expect(mocks.fetch.mock.calls.length).toBe(count + 1);
  });
  it("actionCompletionRefreshesTheCurrentFilterGeneration", async () => {
    let finish: ((response: Response) => void) | undefined;
    const read = mocks.fetch.getMockImplementation()!;
    mocks.fetch.mockImplementation((input: string, options?: RequestInit) =>
      options?.method === "POST"
        ? new Promise<Response>((resolve) => {
            finish = resolve;
          })
        : read(input, options),
    );
    mount();
    await userEvent.click(await screen.findByRole("button", { name: "Recheck Synthetic.mkv" }));
    await userEvent.click(screen.getByText("Health", { selector: "summary" }));
    await userEvent.click(screen.getByRole("checkbox", { name: "Degraded" }));
    await waitFor(() =>
      expect(mocks.fetch.mock.calls.some(([url]) => String(url).includes("health=degraded"))).toBe(
        true,
      ),
    );
    const before = mocks.fetch.mock.calls.length;
    finish!(
      Response.json({ ok: true, result: { status: true, davItemId: row.id, state: "queued" } }),
    );
    await waitFor(() => expect(mocks.fetch.mock.calls.length).toBeGreaterThan(before));
    expect(String(mocks.fetch.mock.calls.at(-1)![0])).toContain("health=degraded");
  });
  it("rendersHealthScheduleAgeAndDetails", async () => {
    mount();
    await screen.findByRole("button", { name: "Play Synthetic.mkv" });
    expect(screen.getByRole("gridcell", { name: "Degraded" })).toBeTruthy();
    expect(screen.getByText("Queued")).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Synthetic.mkv" }));
    expect(screen.getByText("Last played (release)")).toBeTruthy();
  });
  it("rendersSixCapabilityAwareActions", async () => {
    mount();
    await screen.findByRole("button", { name: "Play Synthetic.mkv" });
    for (const label of ["Recheck", "Play", "Download", "Export NZB", "Remove", "Search in Arr"])
      expect(screen.getByRole("button", { name: `${label} Synthetic.mkv` })).toBeTruthy();
    expect(
      screen.getByRole("button", { name: "Export NZB Synthetic.mkv" }).hasAttribute("disabled"),
    ).toBe(true);
  });
  it("readonlyHasNoMutationPath", async () => {
    mocks.readonly.mockReturnValue(true);
    mount();
    await screen.findByRole("button", { name: "Play Synthetic.mkv" });
    for (const label of ["Recheck", "Remove", "Search in Arr"])
      expect(
        screen.getByRole("button", { name: `${label} Synthetic.mkv` }).hasAttribute("disabled"),
      ).toBe(true);
    expect(screen.queryByRole("button", { name: "Remove selected" })).toBeNull();
  });
  it("treeAndListShareAllFilters", async () => {
    mount();
    await screen.findByRole("button", { name: "Play Synthetic.mkv" });
    await userEvent.click(screen.getByText("Health", { selector: "summary" }));
    await userEvent.click(screen.getByRole("checkbox", { name: "Degraded" }));
    await userEvent.click(screen.getByRole("button", { name: "List" }));
    await waitFor(() =>
      expect(
        mocks.fetch.mock.calls.some(
          ([url]) => String(url).includes("health=degraded") && String(url).includes("mode=list"),
        ),
      ).toBe(true),
    );
  });
  it("keepsPlayerMountedAcrossExpandFilterRefreshAndModeSwitch", async () => {
    mount();
    await userEvent.click(await screen.findByRole("button", { name: "Play Synthetic.mkv" }));
    await userEvent.click(screen.getByRole("button", { name: "List" }));
    await screen.findByRole("table", { name: "Files" });
    await userEvent.click(screen.getByRole("button", { name: "Refresh Files" }));
    expect(screen.getByTestId("player")).toBeTruthy();
    expect(mocks.playerMount).toHaveBeenCalledOnce();
  });
  it("requiresSuccessfulDeletionPreview", async () => {
    mount();
    await userEvent.click(await screen.findByRole("button", { name: "Remove Synthetic.mkv" }));
    await waitFor(() =>
      expect(screen.getByRole("button", { name: "Remove" }).hasAttribute("disabled")).toBe(false),
    );
    expect(mocks.fetch.mock.calls.some(([url]) => String(url).includes("expectedDavItemId="))).toBe(
      true,
    );
    expect(mocks.fetch.mock.calls.some(([, options]) => options?.method === "POST")).toBe(false);
  });
  it("keyboardTreeNavigationDoesNotStealInputOrPlayerKeys", async () => {
    mount();
    await screen.findByRole("button", { name: "Play Synthetic.mkv" });
    const search = screen.getByRole("textbox", { name: "Search name or path" });
    search.focus();
    fireEvent.keyDown(search, { key: " " });
    expect(document.activeElement).toBe(search);
  });
  it("strictModeCleanupDoesNotDisableTheRemountedReadCoordinator", async () => {
    mount(true);
    await screen.findByRole("button", { name: "Play Synthetic.mkv" });
    expect(mocks.fetch).not.toHaveBeenCalled();
    await userEvent.click(screen.getByRole("button", { name: "Refresh Files" }));
    await waitFor(() => expect(mocks.fetch).toHaveBeenCalledOnce());
    expect(screen.queryByRole("alert")).toBeNull();
  });
});
