// @vitest-environment jsdom
import { cleanup, render, screen } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router";
import { afterEach, describe, expect, it } from "vitest";
import WatchLayout from "./route";

afterEach(cleanup);

describe("watch layout", () => {
  it("renders the catalog chrome and the active child route", async () => {
    const router = createMemoryRouter(
      [
        {
          path: "/watch",
          element: <WatchLayout />,
          loader: () => ({ query: "night" }),
          children: [{ index: true, element: <p>catalog body</p> }],
        },
      ],
      { initialEntries: ["/watch?q=night"] },
    );

    render(<RouterProvider router={router} />);

    expect(await screen.findByRole("banner")).toBeTruthy();
    // The query keeps the search box filled across navigations.
    const search = screen.getByRole("searchbox", { name: /search movies and series/i });
    expect((search as HTMLInputElement).value).toBe("night");
    expect(screen.getByRole("link", { name: /admin/i }).getAttribute("href")).toBe("/overview");
    expect(screen.getByText("catalog body")).toBeTruthy();
  });
});
