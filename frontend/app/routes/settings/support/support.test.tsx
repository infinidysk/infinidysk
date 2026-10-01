// @vitest-environment jsdom
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { downloadName, SupportSettings } from "./support";

describe("SupportSettings", () => {
  it("keeps the redaction notice inside the technical support pack section", () => {
    const markup = renderToStaticMarkup(<SupportSettings />);
    const parsed = new DOMParser().parseFromString(markup, "text/html");
    const notice = [...parsed.querySelectorAll('[role="alert"]')].find((alert) =>
      alert.textContent?.includes("Review the archive before sharing it."),
    );
    expect(notice?.closest("section")?.querySelector("h2")?.textContent).toBe(
      "Technical support pack",
    );
  });

  it("links to the community Discord in a separate tab", () => {
    const markup = renderToStaticMarkup(<SupportSettings />);

    expect(markup).toContain(
      'href="https://discord.gg/DAya7W6QMa" target="_blank" rel="noopener noreferrer"',
    );
    expect(markup).toContain("Join our Discord");
    expect(markup).toContain("Review the archive before sharing it.");
  });

  it("shows the installed version, project resources, environment guidance, and official sponsor links", () => {
    const markup = renderToStaticMarkup(<SupportSettings version="1.4.2" />);
    expect(markup).toContain("About InfiniDysk");
    expect(markup).toContain("xl:grid-cols-2");
    expect(markup).toContain("1.4.2");
    for (const href of [
      "https://github.com/infinidysk/infinidysk",
      "https://github.com/infinidysk/infinidysk/releases",
      "https://github.com/infinidysk/infinidysk/issues",
      "https://www.infinidysk.com/",
      "https://www.infinidysk.com/configuration/environment-variables/",
      "https://www.infinidysk.com/configuration/headless/",
      "https://github.com/sponsors/hoivikaj",
      "https://www.patreon.com/hoivikaj",
      "https://www.buymeacoffee.com/hoivikaj",
    ]) {
      expect(markup).toContain(`href="${href}" target="_blank" rel="noopener noreferrer"`);
    }
    expect(markup).toContain("NZBDAV_CONFIG__...");
    expect(markup).toContain("Technical support pack");
    expect(markup).toContain("Developer stream tracing");
  });

  it("keeps update details available on Support", () => {
    const markup = renderToStaticMarkup(
      <SupportSettings
        version="1.4.2"
        updateAvailable={{
          kind: "release",
          latestVersion: "1.4.3",
          releaseUrl: "https://example.com/release",
        }}
      />,
    );
    expect(markup).toContain("Update to v1.4.3");
    expect(markup).toContain('href="https://example.com/release"');
  });
});

describe("support pack download name", () => {
  it.each([
    [
      `attachment; filename="ifd-1.2.3___.zip"; filename*=UTF-8''ifd-1.2.3%2B%C3%A9.zip`,
      "ifd-1.2.3+é.zip",
    ],
    [
      `attachment; filename*=UTF-8''support%20%E6%97%A5%E6%9C%AC.zip; filename="fallback.zip"`,
      "support 日本.zip",
    ],
    [
      `attachment; FILENAME*=utf-8'en'support%2520%3B%22%2B.zip; filename="fallback.zip"`,
      'support%20;"+.zip',
    ],
    [`attachment; filename*=UTF-8''support.zip`, "support.zip"],
    ['attachment; filename="fallback.zip"', "fallback.zip"],
    ["attachment; filename=fallback.zip", "fallback.zip"],
    ...[
      "UTF-8''bad%ZZ.zip",
      "UTF-8''bad%.zip",
      "UTF-8''bad%C3%28.zip",
      "UTF-8''",
      "ISO-8859-1''support%E9.zip",
      "support.zip",
    ].map((value) => [`attachment; filename="fallback.zip"; filename*=${value}`, "fallback.zip"]),
    ["attachment", "nzbdav-support-pack.zip"],
    [`attachment; filename*=UTF-8''bad%ZZ.zip`, "nzbdav-support-pack.zip"],
    [null, "nzbdav-support-pack.zip"],
  ])("extracts the download name from %s", (header, expected) => {
    const response = new Response(null, {
      headers: header ? { "content-disposition": header } : {},
    });

    expect(downloadName(response)).toBe(expected);
  });
});
