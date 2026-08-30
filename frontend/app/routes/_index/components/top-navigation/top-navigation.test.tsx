// @vitest-environment jsdom
import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { createMemoryRouter, RouterProvider } from "react-router";
import { TopNavigation } from "./top-navigation";
import styles from "./top-navigation.module.css";

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

    const versionInButton = screen.getByLabelText("App menu").querySelector(".font-mono");
    expect(versionInButton?.className).toContain("text-xs");
    expect(versionInButton?.className).toContain("sm:text-sm");

    const appMenu = screen.getByLabelText("App menu");
    expect(appMenu.className).toContain("border-base-content/10");
    expect(appMenu.className).toContain("bg-base-200");
    expect(appMenu.className).not.toContain("from-primary");
  });

  it("hides the Update available label below the sm breakpoint", () => {
    const router = createMemoryRouter(
      [
        {
          path: "*",
          element: (
            <TopNavigation
              isHamburgerMenuOpen={false}
              onHamburgerMenuClick={() => undefined}
              drawerToggleId="drawer"
              version="1.2.7"
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

    const label = screen.getByText("Update available");
    expect(label.className).toContain("hidden");
    expect(label.className).toContain("sm:inline");
    expect(screen.getByLabelText("Update available").className).toContain("max-sm:btn-square");
    expect(screen.getByLabelText("Update available").className).toContain("border-base-content/10");
    expect(screen.getByLabelText("Update available").className).toContain("bg-clip-padding");
    expect(screen.getByLabelText("Update available").className).toContain(styles.updateAvailable);
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
              version="1.2.7"
              username="admin"
            />
          ),
        },
      ],
      { initialEntries: ["/"] },
    );

    render(<RouterProvider router={router} />);

    const menu = screen.getByLabelText("User menu");
    expect(menu.className).toContain("h-10");
    expect(menu.className).toContain("min-h-10");
    expect(menu.className).toContain("w-10");
    expect(menu.querySelector(".avatar-placeholder > div")?.className).toContain("w-10");
  });
});
