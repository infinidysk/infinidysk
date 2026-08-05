// @vitest-environment jsdom
import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { createMemoryRouter, RouterProvider } from "react-router";
import { LeftNavigation } from "./left-navigation";

afterEach(cleanup);

describe("LeftNavigation settings groups", () => {
    it("renders task-oriented headings and marks the active settings tab", () => {
        const router = createMemoryRouter([
            {
                path: "*",
                element: <LeftNavigation isWatchdogEnabled />,
            },
        ], {
            initialEntries: ["/settings?tab=queue"],
        });

        render(<RouterProvider router={router} />);

        expect(screen.getByText("Providers & Search")).toBeTruthy();
        expect(screen.getByText("Queue & Import")).toBeTruthy();
        expect(screen.getByText("Playback & Files")).toBeTruthy();
        expect(screen.getByText("Automation")).toBeTruthy();
        expect(screen.getByText("Integrations")).toBeTruthy();
        expect(screen.getByText("System")).toBeTruthy();
        const activeQueueLink = screen.getAllByRole("link", { name: "Queue" })
            .find(link => link.getAttribute("href") === "/settings?tab=queue");
        expect(activeQueueLink?.getAttribute("aria-current")).toBe("page");
    });
});
