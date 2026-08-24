// @vitest-environment jsdom
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { afterEach, describe, expect, it } from "vitest";
import { createMemoryRouter, RouterProvider } from "react-router";
import { PageLayout, type RequiredTopNavProps } from "./page-layout";

afterEach(cleanup);

function StatefulNav(_props: RequiredTopNavProps) {
  const [count, setCount] = useState(0);
  return (
    <div>
      <span data-testid="nav-count">{count}</span>
      <button type="button" onClick={() => setCount((value) => value + 1)}>
        increment nav
      </button>
    </div>
  );
}

function Harness() {
  const [tick, setTick] = useState(0);
  return (
    <>
      <span data-testid="parent-tick">{tick}</span>
      <button type="button" onClick={() => setTick((value) => value + 1)}>
        rerender parent
      </button>
      <PageLayout
        topNavComponent={(navProps) => <StatefulNav {...navProps} />}
        leftNavChild={null}
        bodyChild={null}
      />
    </>
  );
}

describe("PageLayout top nav render prop", () => {
  it("keeps nav state when the parent re-renders with a new render-prop identity", async () => {
    const user = userEvent.setup();
    const router = createMemoryRouter(
      [
        {
          path: "*",
          element: <Harness />,
        },
      ],
      { initialEntries: ["/"] },
    );

    render(<RouterProvider router={router} />);

    await user.click(screen.getByRole("button", { name: "increment nav" }));
    expect(screen.getByTestId("nav-count").textContent).toBe("1");

    await user.click(screen.getByRole("button", { name: "rerender parent" }));
    expect(screen.getByTestId("parent-tick").textContent).toBe("1");
    expect(screen.getByTestId("nav-count").textContent).toBe("1");
  });
});
