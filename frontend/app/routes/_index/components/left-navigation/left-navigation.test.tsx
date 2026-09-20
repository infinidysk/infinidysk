// @vitest-environment jsdom
import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { createMemoryRouter, RouterProvider } from "react-router";
import { LeftNavigation } from "./left-navigation";

afterEach(cleanup);

describe("LeftNavigation settings groups", () => {
  it.each(["1.4.2", "dev-260919.2224"])("pins %s in a footer link to Support", (version) => {
    const router = createMemoryRouter(
      [
        {
          path: "*",
          element: (
            <LeftNavigation
              version={version}
              updateAvailable={{
                kind: "release",
                latestVersion: "1.4.3",
                releaseUrl: "https://example.com/release",
              }}
            />
          ),
        },
      ],
      { initialEntries: ["/overview"] },
    );
    render(<RouterProvider router={router} />);
    const trigger = screen.getByRole("link", {
      name: `InfiniDysk ${version}. Support and about. Update available`,
    });
    expect(trigger.textContent).toContain(version);
    expect(trigger.closest("footer")?.className).toContain("shrink-0");
    expect(trigger.getAttribute("href")).toBe("/settings?tab=support");
    expect(trigger.closest("details")).toBeNull();
    expect(screen.getByRole("navigation", { name: "Main" }).className).toContain("overflow-y-auto");
    expect(trigger.querySelector(".text-warning")).not.toBeNull();
    expect(trigger.className).toContain("border-warning");
    expect(trigger.className).not.toContain("border-base-content/20");
  });

  it("renders task-oriented headings and marks the active settings tab", () => {
    const router = createMemoryRouter(
      [
        {
          path: "*",
          element: <LeftNavigation isWatchdogEnabled />,
        },
      ],
      {
        initialEntries: ["/settings?tab=queue"],
      },
    );

    render(<RouterProvider router={router} />);

    const versionLink = screen.getByRole("link", { name: /Support and about/ });
    expect(versionLink.className).toContain("border-base-content/20");
    expect(versionLink.className).not.toContain("border-warning");
    expect(screen.getByText("Providers & Search")).toBeTruthy();
    expect(screen.getByText("Queue & Import")).toBeTruthy();
    expect(screen.getByText("Playback & Files")).toBeTruthy();
    expect(screen.getByText("Automation")).toBeTruthy();
    expect(screen.getByText("Integrations")).toBeTruthy();
    expect(screen.getByText("System")).toBeTruthy();
    const activeQueueLink = screen
      .getAllByRole("link", { name: "Queue" })
      .find((link) => link.getAttribute("href") === "/settings?tab=queue");
    expect(activeQueueLink?.getAttribute("aria-current")).toBe("page");
  });
});
