// @vitest-environment jsdom

import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it } from "vitest";
import { Tooltip } from "./feedback";

afterEach(() => {
  cleanup();
});

describe("Tooltip", () => {
  it("keeps closed help text out of the accessibility tree until hover or focus", async () => {
    const user = userEvent.setup();
    render(
      <Tooltip content="Helpful details">
        <button type="button">More info</button>
      </Tooltip>,
    );

    const tooltip = screen.getByRole("tooltip", { hidden: true });
    const trigger = screen.getByRole("button", { name: "More info" });
    expect(tooltip.className).toContain("break-words");
    expect(tooltip.getAttribute("aria-hidden")).toBe("true");
    expect(trigger.getAttribute("aria-describedby")).toBeNull();

    await user.hover(trigger);
    expect(tooltip.getAttribute("aria-hidden")).toBe("false");
    expect(trigger.getAttribute("aria-describedby")).toBe(tooltip.id);

    await user.unhover(trigger);
    expect(tooltip.getAttribute("aria-hidden")).toBe("true");
    expect(trigger.getAttribute("aria-describedby")).toBeNull();

    await user.tab();
    expect(tooltip.getAttribute("aria-hidden")).toBe("false");
    expect(trigger.getAttribute("aria-describedby")).toBe(tooltip.id);
  });
});
