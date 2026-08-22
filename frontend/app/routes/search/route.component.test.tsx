// @vitest-environment jsdom
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ComponentProps } from "react";
import { createRoutesStub } from "react-router";
import { afterEach, describe, expect, it, vi } from "vitest";
import Search from "./route";

vi.mock("~/auth/authorization", () => ({
  useIsReadOnly: () => false,
}));

afterEach(cleanup);

const loaderData = {
  q: "example",
  data: {
    indexers: [],
    results: [
      {
        indexer: "Example Indexer",
        title: "Example Release",
        nzbUrl: "https://indexer.example/nzb/123",
        size: 1024,
        posted: null,
      },
    ],
  },
};

describe("Search Mount", () => {
  it("posts the selected NZB URL and name to the route action", async () => {
    let submittedFormData: FormData | undefined;
    const action = vi.fn(async ({ request }: { request: Request }) => {
      submittedFormData = await request.formData();
      return { ok: true, nzoId: "SABnzbd_nzo_1" };
    });
    const Stub = createRoutesStub([
      {
        path: "/search",
        Component: () => (
          <Search {...({ loaderData } as unknown as ComponentProps<typeof Search>)} />
        ),
        action,
      },
    ]);

    render(<Stub initialEntries={["/search?q=example"]} />);

    await userEvent.setup().click(screen.getByRole("button", { name: "Mount" }));

    await waitFor(() => expect(action).toHaveBeenCalledTimes(1));
    const [{ request }] = action.mock.calls[0]!;
    expect(request.method).toBe("POST");
    expect(Object.fromEntries(submittedFormData!)).toEqual({
      nzbUrl: "https://indexer.example/nzb/123",
      nzbName: "Example Release",
    });
    await waitFor(() => expect(screen.getByRole("button", { name: "Mounted" })).toBeTruthy());
  });
});
