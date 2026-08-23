import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it, vi } from "vitest";
import { SectionLoadError } from "./section-load-error";

describe("SectionLoadError", () => {
  it("names the failed section and offers a retry action", () => {
    const markup = renderToStaticMarkup(
      <SectionLoadError label="provider stats" onRetry={vi.fn()} />,
    );

    expect(markup).toContain("Could not load provider stats");
    expect(markup).toContain("Retry");
    expect(markup).toContain('role="alert"');
  });
});
