import { afterEach, describe, expect, it, vi } from "vitest";
import { loadPlexBootstrap, plexRequest, validMappings, validServer } from "./plex-api";

describe("Plex browser API boundary", () => {
  it("allows the backend to resolve a manual server machine identity", () => {
    expect(
      validServer({
        id: "",
        name: "Home",
        url: "http://localhost:32400",
        token: "secret",
        enabled: true,
        pathMappings: [],
      }),
    ).toBe(true);
  });
  afterEach(() => vi.unstubAllGlobals());
  it("bootstraps serially so the owner cookie exists before servers are loaded", async () => {
    let release!: (value: Response) => void;
    const fetcher = vi
      .fn<(url: string, init: RequestInit) => Promise<Response>>()
      .mockImplementationOnce(
        () =>
          new Promise<Response>((resolve) => {
            release = resolve;
          }),
      )
      .mockResolvedValueOnce(new Response(JSON.stringify({ servers: [] })));
    vi.stubGlobal("fetch", fetcher);
    const pending = loadPlexBootstrap();
    expect(fetcher).toHaveBeenCalledTimes(1);
    release(new Response(JSON.stringify({ accounts: [] })));
    expect(await pending).toEqual({ accounts: [], servers: [] });
    expect(fetcher.mock.calls[0]?.[0]).toContain("/api/plex/accounts");
    expect(fetcher.mock.calls[1]?.[0]).toContain("/api/plex/servers");
  });
  it("uses POST bodies and does not echo unsafe upstream bodies on error", async () => {
    const fetcher = vi
      .fn<(url: string, init: RequestInit) => Promise<Response>>()
      .mockResolvedValue(new Response("secret-token", { status: 502 }));
    vi.stubGlobal("fetch", fetcher);
    await expect(plexRequest("home/switch", { handle: "opaque", pin: "1234" })).rejects.toThrow(
      "Plex request failed",
    );
    expect(fetcher.mock.calls[0]?.[0]).not.toContain("1234");
    expect(fetcher.mock.calls[0]?.[1].body).toContain("1234");
  });
  it("accepts exactly one absolute DAV or local mapping target", () => {
    expect(validMappings([{ plexPath: "/Plex", davPath: "/content" }])).toBe(true);
    expect(validMappings([{ plexPath: "C:\\Plex", localPath: "/mnt/library" }])).toBe(true);
    expect(validMappings([{ plexPath: "/Plex", davPath: "/content", localPath: "/mnt" }])).toBe(
      false,
    );
    expect(validMappings([{ plexPath: "/Plex", davPath: "/content/../escape" }])).toBe(false);
  });
});
