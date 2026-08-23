// @vitest-environment jsdom
import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { createMemoryRouter, RouterProvider } from "react-router";
import { TopNavigation } from "./top-navigation";

vi.mock("../live-usenet-connections/live-usenet-connections", () => ({
  LiveUsenetConnections: () => null,
}));

afterEach(cleanup);

function renderTopNavigation(version = "0.8.0") {
  const router = createMemoryRouter(
    [
      {
        path: "*",
        element: (
          <TopNavigation
            isHamburgerMenuOpen={false}
            onHamburgerMenuClick={() => undefined}
            drawerToggleId="drawer"
            version={version}
          />
        ),
      },
    ],
    { initialEntries: ["/"] },
  );

  return render(<RouterProvider router={router} />);
}

describe("TopNavigation version summary", () => {
  it("hides the channel label and separator below the sm breakpoint", () => {
    renderTopNavigation();

    const channelLabel = screen.getByText("Stable");
    expect(channelLabel.className).toContain("hidden");
    expect(channelLabel.className).toContain("sm:inline");

    const separator = channelLabel.nextElementSibling;
    expect(separator).toBeInstanceOf(HTMLSpanElement);
    expect(separator?.getAttribute("aria-hidden")).toBe("true");
    expect(separator?.className).toContain("hidden");
    expect(separator?.className).toContain("sm:block");

    expect(screen.getAllByText("0.8.0")).toHaveLength(2);
    expect(screen.getByText("InfiniDysk Stable")).toBeTruthy();
  });
});
