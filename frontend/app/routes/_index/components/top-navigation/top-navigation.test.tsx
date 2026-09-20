// @vitest-environment jsdom
import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { createMemoryRouter, RouterProvider } from "react-router";
import { TopNavigation } from "./top-navigation";

vi.mock("../live-usenet-connections/live-usenet-connections", () => ({
  LiveUsenetConnections: () => null,
}));

vi.mock("./header-alerts", () => ({
  HeaderAlerts: () => <button aria-label="Alerts" />,
}));

vi.mock("./live-read-count", () => ({
  LiveReadCount: () => null,
}));

afterEach(cleanup);

function renderTopNavigation() {
  const router = createMemoryRouter(
    [
      {
        path: "*",
        element: (
          <TopNavigation
            isHamburgerMenuOpen={false}
            onHamburgerMenuClick={() => undefined}
            drawerToggleId="drawer"
          />
        ),
      },
    ],
    { initialEntries: ["/"] },
  );

  return render(<RouterProvider router={router} />);
}

describe("TopNavigation", () => {
  it("keeps the version menu out of the header", () => {
    renderTopNavigation();
    expect(screen.queryByText("Stable", { exact: true })).toBeNull();
    expect(screen.queryByText("Dev", { exact: true })).toBeNull();
    expect(screen.queryByLabelText("App menu")).toBeNull();
  });

  it("does not add a separate update button to the header", () => {
    const router = createMemoryRouter(
      [
        {
          path: "*",
          element: (
            <TopNavigation
              isHamburgerMenuOpen={false}
              onHamburgerMenuClick={() => undefined}
              drawerToggleId="drawer"
              updateAvailable={{
                kind: "release",
                latestVersion: "1.2.8",
                releaseUrl: "https://example.com",
              }}
            />
          ),
        },
      ],
      { initialEntries: ["/"] },
    );

    render(<RouterProvider router={router} />);

    expect(screen.queryByLabelText("Update available")).toBeNull();
    expect(screen.getByLabelText("Alerts")).toBeTruthy();
  });

  it("sizes the user avatar to the header control height", () => {
    const router = createMemoryRouter(
      [
        {
          path: "*",
          element: (
            <TopNavigation
              isHamburgerMenuOpen={false}
              onHamburgerMenuClick={() => undefined}
              drawerToggleId="drawer"
              username="admin"
            />
          ),
        },
      ],
      { initialEntries: ["/"] },
    );

    render(<RouterProvider router={router} />);

    const menu = screen.getByLabelText("User menu");
    expect(screen.getByLabelText("Alerts").nextElementSibling?.tagName).toBe("FORM");
    expect(menu.className).toContain("h-10");
    expect(menu.className).toContain("min-h-10");
    expect(menu.className).toContain("w-10");
    expect(menu.querySelector(".avatar-placeholder > div")?.className).toContain("w-10");
  });
});
