import { beforeEach, describe, expect, it, vi } from "vitest";
import { action, shouldRevalidate } from "./route";

const { addNzbFromUrlMock } = vi.hoisted(() => ({
  addNzbFromUrlMock: vi.fn(),
}));

vi.mock("~/clients/backend-client.server", () => ({
  backendClient: {
    addNzbFromUrl: addNzbFromUrlMock,
  },
}));

function mountRequest(nzbUrl?: string, nzbName?: string): Request {
  const formData = new FormData();
  if (nzbUrl !== undefined) formData.set("nzbUrl", nzbUrl);
  if (nzbName !== undefined) formData.set("nzbName", nzbName);
  return new Request("http://localhost/search", { method: "POST", body: formData });
}

describe("Search route action", () => {
  beforeEach(() => {
    addNzbFromUrlMock.mockReset();
  });

  it("adds the submitted NZB URL", async () => {
    addNzbFromUrlMock.mockResolvedValueOnce("SABnzbd_nzo_1");

    await expect(
      action({
        request: mountRequest("https://indexer.example/nzb/123", "Example Release"),
      } as Parameters<typeof action>[0]),
    ).resolves.toEqual({ ok: true, nzoId: "SABnzbd_nzo_1" });

    expect(addNzbFromUrlMock).toHaveBeenCalledWith(
      "https://indexer.example/nzb/123",
      "Example Release",
    );
  });

  it("rejects a submission without both NZB fields", async () => {
    await expect(
      action({ request: mountRequest("https://indexer.example/nzb/123") } as Parameters<
        typeof action
      >[0]),
    ).resolves.toEqual({ ok: false, error: "Missing nzbUrl or nzbName" });

    expect(addNzbFromUrlMock).not.toHaveBeenCalled();
  });

  it("returns a backend add failure", async () => {
    addNzbFromUrlMock.mockRejectedValueOnce(new Error("provider unavailable"));

    await expect(
      action({
        request: mountRequest("https://indexer.example/nzb/123", "Example Release"),
      } as Parameters<typeof action>[0]),
    ).resolves.toEqual({ ok: false, error: "provider unavailable" });
  });
});

describe("Search route revalidation", () => {
  it("skips revalidation after mounting an NZB", () => {
    const formData = new FormData();
    formData.set("nzbUrl", "https://indexer.example/nzb/123");

    expect(
      shouldRevalidate({
        formData,
        formMethod: "POST",
        defaultShouldRevalidate: true,
      }),
    ).toBe(false);
  });

  it("revalidates normal search navigations", () => {
    expect(
      shouldRevalidate({
        formData: new FormData(),
        formMethod: "GET",
        defaultShouldRevalidate: true,
      }),
    ).toBe(true);
  });
});
