// @vitest-environment jsdom

import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { LegacyImageBanner } from "./legacy-image-banner";

afterEach(() => {
  cleanup();
});

describe("LegacyImageBanner", () => {
  it("warns about the deprecated image path when the legacy flag is set", () => {
    render(<LegacyImageBanner isLegacyImage={true} />);

    expect(screen.getByText("This image path is deprecated")).toBeTruthy();
    expect(screen.getByRole("link", { name: "Read the rename FAQ" }).getAttribute("href"))
      .toBe("https://www.infinidysk.com/community/renaming-to-infinidysk/");
    // Persistent by design: no dismiss control.
    expect(screen.queryByRole("button")).toBeNull();
  });

  it("renders nothing on the new image path", () => {
    const { container } = render(<LegacyImageBanner isLegacyImage={false} />);

    expect(container.innerHTML).toBe("");
  });
});
