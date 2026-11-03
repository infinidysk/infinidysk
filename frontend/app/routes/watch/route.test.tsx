// @vitest-environment jsdom
import { cleanup, render, screen } from "@testing-library/react";
import { createMemoryRouter, Outlet, RouterProvider } from "react-router";
import { afterEach, describe, expect, it } from "vitest";
import type { AppOutletContext } from "~/auth/authorization";
import WatchLayout from "./route";

afterEach(cleanup);

function renderWatch(context: AppOutletContext) {
  const router = createMemoryRouter(
    [
      {
        path: "/",
        element: <Outlet context={context} />,
        children: [
          {
            path: "watch",
            element: <WatchLayout />,
            loader: () => ({ query: "night" }),
            children: [{ index: true, element: <p>catalog body</p> }],
          },
        ],
      },
    ],
    { initialEntries: ["/watch?q=night"] },
  );

  return render(<RouterProvider router={router} />);
}

const adminContext: AppOutletContext = {
  role: "admin",
  isOidcEnabled: false,
  serviceProvider: null,
  viewerProfile: null,
};

describe("watch layout", () => {
  it("renders the catalog chrome and the active child route", async () => {
    renderWatch(adminContext);

    expect(await screen.findByRole("banner")).toBeTruthy();
    // The query keeps the search box filled across navigations.
    const search = screen.getByRole("searchbox", { name: /search movies and series/i });
    expect((search as HTMLInputElement).value).toBe("night");
    expect(screen.getByRole("link", { name: /admin/i }).getAttribute("href")).toBe("/overview");
    expect(screen.getByText("catalog body")).toBeTruthy();
  });

  it("swaps the admin link for the profile menu in a profile session", async () => {
    renderWatch({
      role: "profile",
      isOidcEnabled: false,
      serviceProvider: null,
      viewerProfile: { id: 7, name: "Kids" },
    });

    expect(await screen.findByText("Kids")).toBeTruthy();
    // A profile session never gets a way back into the admin surface.
    expect(screen.queryByRole("link", { name: /admin/i })).toBeNull();
  });
});
