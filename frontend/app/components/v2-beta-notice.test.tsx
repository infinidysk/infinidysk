// @vitest-environment jsdom

import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it } from "vitest";
import { V2_BETA_NOTICE_KEY, V2BetaNotice } from "./v2-beta-notice";

afterEach(() => {
  cleanup();
  localStorage.clear();
});

describe("V2BetaNotice", () => {
  it("announces the beta and links to the 2.0 page", async () => {
    render(<V2BetaNotice />);

    expect(await screen.findByText("infinidysk 2.0 is in beta")).toBeTruthy();
    expect(screen.getByRole("link", { name: "Read about 2.0" }).getAttribute("href")).toBe(
      "https://www.infinidysk.com/v2/whats-new/",
    );
  });

  it("stays hidden after it is dismissed", async () => {
    const { unmount } = render(<V2BetaNotice />);

    await userEvent.click(await screen.findByRole("button", { name: "Dismiss" }));

    expect(screen.queryByText("infinidysk 2.0 is in beta")).toBeNull();
    expect(localStorage.getItem(V2_BETA_NOTICE_KEY)).toBe("true");

    unmount();
    const { container } = render(<V2BetaNotice />);
    expect(container.innerHTML).toBe("");
  });
});
