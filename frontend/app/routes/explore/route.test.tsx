import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ShouldRevalidateFunctionArgs } from "react-router";
import type { Route } from "./+types/route";
import { makeFilesPage } from "./files-fixtures";
const mocks = vi.hoisted(() => ({ page: vi.fn(), directory: vi.fn() }));
vi.mock("~/auth/authentication.server", () => ({ isAuthenticated: () => Promise.resolve(true) }));
vi.mock("~/auth/downloads.server", () => ({ getDownloadKey: () => "synthetic" }));
vi.mock("~/clients/files-page.server", () => ({ loadFilesPage: mocks.page }));
vi.mock("../../../server/runtime-config", () => ({
  getFrontendRuntimeConfig: () => ({ frontendBackendApiKey: "synthetic" }),
}));
vi.mock("~/clients/backend-client.server", async (importOriginal) => ({
  ...(await importOriginal<typeof import("~/clients/backend-client.server")>()),
  backendClient: { listWebdavDirectory: mocks.directory },
}));
import { loader, shouldRevalidate } from "./route";
beforeEach(() => {
  vi.clearAllMocks();
  mocks.page.mockResolvedValue(makeFilesPage([]));
  mocks.directory.mockResolvedValue([]);
});
const args = (path: string) =>
  ({
    request: new Request("http://localhost/explore"),
    params: { "*": path },
  }) as unknown as Route.LoaderArgs;
describe("Explore compatibility", () => {
  it("rootUsesCanonicalFilesBody", async () => {
    expect(await loader(args(""))).toMatchObject({ kind: "files", scopePath: "/content" });
    expect(mocks.directory).not.toHaveBeenCalled();
  });
  it("contentDeepLinkInitializesScope", async () => {
    expect(await loader(args("content/tv/Release"))).toMatchObject({
      kind: "files",
      scopePath: "/content/tv/Release",
    });
  });
  it("queryChangesDoNotReloadCanonicalRoute", () => {
    expect(
      shouldRevalidate({
        currentUrl: new URL("http://localhost/explore"),
        nextUrl: new URL("http://localhost/explore?mode=list"),
        defaultShouldRevalidate: true,
      } as ShouldRevalidateFunctionArgs),
    ).toBe(false);
  });
  it("systemMountDeepLinksKeepLegacyBehavior", async () => {
    for (const path of ["nzbs", "completed-symlinks", ".ids"]) {
      expect(await loader(args(path))).toMatchObject({ kind: "legacy" });
      expect(mocks.directory).toHaveBeenLastCalledWith(path);
    }
    expect(mocks.page).not.toHaveBeenCalled();
  });
  it("literalPercentAndMalformedSplatKeepExistingSemantics", async () => {
    expect(await loader(args("content/literal%2C"))).toMatchObject({
      scopePath: "/content/literal%2C",
    });
    expect(await loader(args("content//invalid"))).toMatchObject({
      kind: "legacy",
      data: { error: "not-found" },
    });
  });
});
