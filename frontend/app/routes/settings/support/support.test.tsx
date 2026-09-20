// @vitest-environment jsdom
import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { SupportSettings } from "./support";

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
