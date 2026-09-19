import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { SupportSettings } from "./support";

describe("SupportSettings", () => {
  it("links to the community Discord in a separate tab", () => {
    const markup = renderToStaticMarkup(<SupportSettings />);

    expect(markup).toContain(
      'href="https://discord.gg/DAya7W6QMa" target="_blank" rel="noopener noreferrer"',
    );
    expect(markup).toContain("Join our Discord");
    expect(markup).toContain("Review the archive before sharing it.");
  });
});
