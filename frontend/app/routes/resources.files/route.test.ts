import { beforeEach, describe, expect, it, vi } from "vitest";
import { makeFileRow, makeFilesPage } from "../explore/files-fixtures";
import { filesResourcePageSchema } from "~/clients/files-contract";
const mocks = vi.hoisted(() => ({
  disabled: false,
  base: "",
  user: vi.fn(),
  browse: vi.fn(),
  recheck: vi.fn(),
  search: vi.fn(),
  preview: vi.fn(),
  remove: vi.fn(),
  sign: vi.fn(),
}));
vi.mock("~/auth/authentication.server", () => ({
  getSessionUser: mocks.user,
  get IS_FRONTEND_AUTH_DISABLED() {
    return mocks.disabled;
  },
}));
vi.mock("~/auth/downloads.server", async (importOriginal) => ({
  getDownloadKey: mocks.sign.mockImplementation(
    (await importOriginal<typeof import("~/auth/downloads.server")>()).getDownloadKey,
  ),
}));
vi.mock("~/utils/url-base", () => ({ withUrlBase: (path: string) => mocks.base + path }));
vi.mock("../../../server/runtime-config", () => ({
  getFrontendRuntimeConfig: () => ({ frontendBackendApiKey: "synthetic-key" }),
}));
vi.mock("~/clients/backend-client.server", async (importOriginal) => ({
  ...(await importOriginal<typeof import("~/clients/backend-client.server")>()),
  backendClient: {
    browseFiles: mocks.browse,
    recheckFile: mocks.recheck,
    searchFileInArr: mocks.search,
    previewFileRemoval: mocks.preview,
    removeFile: mocks.remove,
  },
}));
import { loader, action } from "./route";
import {
  BackendApiError,
  BackendContractError,
  BackendUnavailableError,
} from "~/clients/backend-client.server";
beforeEach(() => {
  vi.clearAllMocks();
  mocks.disabled = false;
  mocks.base = "";
  mocks.user.mockResolvedValue({ role: "admin" });
  mocks.browse.mockResolvedValue(makeFilesPage([makeFileRow()]));
});
function request(intent: string, extras: Record<string, string> = {}) {
  const body = new FormData();
  body.set("intent", intent);
  body.set("davItemId", makeFileRow().id!);
  for (const [key, value] of Object.entries(extras)) body.set(key, value);
  return new Request("http://localhost/resources/files", { method: "POST", body });
}
describe("Files resource", () => {
  it("authDisabledFollowsExistingPolicy", async () => {
    mocks.disabled = true;
    mocks.user.mockResolvedValue(null);
    mocks.recheck.mockResolvedValue({ status: true, davItemId: makeFileRow().id, state: "queued" });
    const response = await action({ request: request("recheck") });
    expect(response.data).toMatchObject({ ok: true });
  });
  it("supportsUrlBaseWithoutDoublePrefix", async () => {
    mocks.base = "/synthetic-base";
    const response = await loader({
      request: new Request("http://localhost/synthetic-base/resources/files"),
    });
    const url = filesResourcePageSchema.parse(response.data).rows[0]!.previewUrl!;
    expect(url).toMatch(
      /^\/synthetic-base\/view\/content\/Synthetic.mkv\?downloadKey=[a-f0-9]{64}/,
    );
    expect(url).not.toContain("/synthetic-base/synthetic-base");
    expect(mocks.sign).toHaveBeenCalledWith("content/Synthetic.mkv", "synthetic-key");
  });
  it("anonymousRequestsNeverCallBackendOrSignUrls", async () => {
    mocks.user.mockResolvedValue(null);
    await expect(
      loader({ request: new Request("http://localhost/resources/files") }),
    ).rejects.toMatchObject({ init: { status: 401 } });
    expect(mocks.browse).not.toHaveBeenCalled();
    expect(mocks.sign).not.toHaveBeenCalled();
  });
  it("readonlyCanBrowseButCannotMutateOrPreviewRemoval", async () => {
    mocks.user.mockResolvedValue({ role: "readonly" });
    await loader({ request: new Request("http://localhost/resources/files") });
    expect(mocks.browse).toHaveBeenCalledOnce();
    await expect(action({ request: request("recheck") })).rejects.toMatchObject({
      init: { status: 403 },
    });
    await expect(
      loader({ request: new Request("http://localhost/resources/files?operation=delete-preview") }),
    ).rejects.toMatchObject({ init: { status: 403 } });
    expect(mocks.recheck).not.toHaveBeenCalled();
    expect(mocks.preview).not.toHaveBeenCalled();
  });
  it("signsDecodedPathAndEncodesEachUrlSegmentOnce", async () => {
    const path = "/content/literal %2C/#? \\ \u00e9.mkv";
    mocks.browse.mockResolvedValue(
      makeFilesPage([makeFileRow({ path, name: "#? \\ \u00e9.mkv" })]),
    );
    const result = await loader({ request: new Request("http://localhost/resources/files") });
    expect(mocks.sign).toHaveBeenCalledWith(path.slice(1), "synthetic-key");
    expect(filesResourcePageSchema.parse(result.data).rows[0]?.previewUrl).toContain(
      "literal%20%252C/%23%3F%20%5C%20%C3%A9.mkv",
    );
  });
  it("rejectsUnknownIntentAndUnconfirmedMutation", async () => {
    await expect(action({ request: request("bad") })).rejects.toMatchObject({
      init: { status: 400 },
    });
    await expect(
      action({ request: request("arr-search", { confirmed: "false" }) }),
    ).rejects.toMatchObject({ init: { status: 400 } });
    expect(mocks.search).not.toHaveBeenCalled();
  });
  it("passesAbortSignalAndReturnsSanitizedErrors", async () => {
    const input = request("recheck");
    mocks.recheck.mockRejectedValue(
      new BackendApiError("ignored", 409, "Conflict", "File changed."),
    );
    const result = await action({ request: input });
    expect(mocks.recheck).toHaveBeenCalledWith(makeFileRow().id, input.signal);
    expect(result).toMatchObject({
      data: { ok: false, error: "File changed." },
      init: { status: 409 },
    });
    for (const error of [
      new BackendContractError("secret"),
      new BackendUnavailableError("secret"),
    ]) {
      mocks.recheck.mockRejectedValue(error);
      expect(JSON.stringify((await action({ request: request("recheck") })).data)).not.toContain(
        "secret",
      );
    }
  });
  it("preservesArrPartialOutcome", async () => {
    mocks.search.mockResolvedValue({
      status: true,
      davItemId: makeFileRow().id,
      outcome: "partial",
      results: [],
    });
    const result = await action({ request: request("arr-search", { confirmed: "true" }) });
    expect(result.data).toMatchObject({ ok: true, result: { outcome: "partial" } });
    expect(mocks.remove).not.toHaveBeenCalled();
  });
});
