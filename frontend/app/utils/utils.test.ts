import { describe, expect, it, vi } from "vitest";
import { clientIdentityTooltip, clientLabelFromUserAgent } from "./client-label";
import { isMaskedSecret } from "./config-mask";
import { formatFileSize } from "./file-size";
import {
  getExploreBreadcrumbHref,
  getExploreContentLink,
  getLeafDirectoryName,
  parseExploreWebdavPath,
} from "./path";
import { className, classNames } from "./styling";
import { receiveMessage } from "./websocket-util";

describe("clientLabelFromUserAgent", () => {
  it.each([
    [undefined, "Unknown"],
    [null, "Unknown"],
    ["", "Unknown"],
    ["  ", "Unknown"],
    ["rclone/1.68.0", "rclone"],
    ["Mozilla/5.0 PlexMediaServer/1.40", "Plex"],
    ["EmbyServer/4.8", "Emby"],
    ["Jellyfin-Server/10.9", "Jellyfin"],
    ["Infuse-Direct/7.0", "Infuse"],
    ["VLC/3.0.20 LibVLC/3.0.20", "VLC"],
    ["Kodi/21.0", "Kodi"],
  ])("maps %s to %s", (ua, expected) => {
    expect(clientLabelFromUserAgent(ua)).toBe(expected);
  });

  it("truncates unknown long user agents", () => {
    const ua = "SomeCustomClient/1.2.3 (very-long-build-id-abcdefghijklmnop)";
    const label = clientLabelFromUserAgent(ua);
    expect(label.length).toBe(28);
    expect(label.endsWith("…")).toBe(true);
    expect(label.startsWith("SomeCustomClient")).toBe(true);
  });
});

describe("clientIdentityTooltip", () => {
  it("joins UA and IP when both present", () => {
    expect(clientIdentityTooltip("rclone/1.68", "10.0.0.5")).toBe("rclone/1.68 · 10.0.0.5");
  });

  it("returns undefined when neither is present", () => {
    expect(clientIdentityTooltip(null, undefined)).toBeUndefined();
  });
});

describe("formatFileSize", () => {
  it.each([
    [undefined, "unknown size"],
    [null, "unknown size"],
    [0, "0 B"],
    [1024, "1 KB"],
    [1536, "1.5 KB"],
    [1024 ** 3, "1 GB"],
  ])("formats %s bytes as %s", (bytes, expected) => {
    expect(formatFileSize(bytes)).toBe(expected);
  });
});

describe("getLeafDirectoryName", () => {
  it.each([
    ["/view/movies/Alien", "Alien"],
    ["/view/movies/Alien/", "Alien"],
    ["C:\\media\\Alien\\", "Alien"],
    ["Alien", "Alien"],
  ])("gets the leaf from %s", (path, expected) => {
    expect(getLeafDirectoryName(path)).toBe(expected);
  });
});

describe("getExploreContentLink", () => {
  it("builds an explore content URL", () => {
    expect(getExploreContentLink("/completed/movies/Alien", "movies"))
      .toBe("/explore/content/movies/Alien");
  });

  it("encodes category and folder segments", () => {
    expect(getExploreContentLink("/completed/tv shows/Show Name", "tv shows"))
      .toBe("/explore/content/tv%20shows/Show%20Name");
  });

  it("returns null when storage or category is missing", () => {
    expect(getExploreContentLink(null, "movies")).toBeNull();
    expect(getExploreContentLink("/completed/movies/Alien", null)).toBeNull();
    expect(getExploreContentLink("", "movies")).toBeNull();
  });

  it("returns null when storage or category is whitespace-only", () => {
    expect(getExploreContentLink("   ", "movies")).toBeNull();
    expect(getExploreContentLink("/completed/movies/Alien", "   ")).toBeNull();
    expect(getExploreContentLink("/completed/movies/Alien", "")).toBeNull();
  });
});

describe("getExploreBreadcrumbHref", () => {
  it("returns the Explore root for the home breadcrumb", () => {
    expect(getExploreBreadcrumbHref(["content"], -1)).toBe("/explore");
  });

  it("encodes each selected directory independently", () => {
    const directories = ["content", "My#1 Hits", "100%", "A?B", "tv shows", "日本語"];
    const href = getExploreBreadcrumbHref(directories, directories.length - 1);

    expect(href).toBe("/explore/content/My%231%20Hits/100%25/A%3FB/tv%20shows/%E6%97%A5%E6%9C%AC%E8%AA%9E");
    expect(parseExploreWebdavPath(href.slice("/explore/".length))).toEqual({
      ok: true,
      path: directories.join("/"),
    });
  });
});

describe("parseExploreWebdavPath", () => {
  it("accepts a valid encoded path", () => {
    expect(parseExploreWebdavPath("content/tv%20shows/Alien")).toEqual({
      ok: true,
      path: "content/tv shows/Alien",
    });
  });

  it("accepts the WebDAV root", () => {
    expect(parseExploreWebdavPath("")).toEqual({ ok: true, path: "" });
    expect(parseExploreWebdavPath("/")).toEqual({ ok: true, path: "" });
  });

  it("rejects empty path segments from double slashes", () => {
    expect(parseExploreWebdavPath("content//Release")).toEqual({ ok: false });
    expect(parseExploreWebdavPath("content//")).toEqual({ ok: false });
  });

    it("rejects malformed percent-encoding", () => {
        expect(parseExploreWebdavPath("content/%E0%A4%A")).toEqual({ ok: false });
    });

    it("keeps literal percent sequences when the path is already decoded", () => {
        expect(parseExploreWebdavPath(
            "content/tv/S02E14.Such.Sweet.Sorrow%2C.Part.2.1080",
            { decode: false },
        )).toEqual({
            ok: true,
            path: "content/tv/S02E14.Such.Sweet.Sorrow%2C.Part.2.1080",
        });
        expect(parseExploreWebdavPath("content/100%", { decode: false })).toEqual({
            ok: true,
            path: "content/100%",
        });
    });

    it("decodes percent sequences when the input is still encoded", () => {
        expect(parseExploreWebdavPath("content/Sorrow%2C.Part.2")).toEqual({
            ok: true,
            path: "content/Sorrow,.Part.2",
        });
    });
});

describe("secret masking", () => {
  it("recognizes only masked secret values", () => {
    expect(isMaskedSecret("__NZBDAV_SECRET_MASK_V1__:abc")).toBe(true);
    expect(isMaskedSecret("abc")).toBe(false);
    expect(isMaskedSecret(undefined)).toBe(false);
  });
});

describe("class name helpers", () => {
  const values: (string | false | null | undefined)[] = [
    "card",
    false,
    null,
    undefined,
    "active",
  ];

  it("joins truthy class names", () => {
    expect(classNames(values)).toBe("card active");
  });

  it("returns a className property", () => {
    expect(className(values)).toEqual({ className: "card active" });
  });
});

describe("receiveMessage", () => {
  it("parses a websocket message and forwards its values", () => {
    const onMessage = vi.fn();
    const handler = receiveMessage(onMessage);

    handler({
      data: JSON.stringify({ Topic: "queue", Message: "updated" }),
    } as MessageEvent);

    expect(onMessage).toHaveBeenCalledOnce();
    expect(onMessage).toHaveBeenCalledWith("queue", "updated");
  });
});
