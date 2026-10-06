// @vitest-environment jsdom

import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it } from "vitest";
import { Button } from "./button";
import { PortalTooltip, Tooltip } from "./feedback";

afterEach(() => {
  cleanup();
});

describe("PortalTooltip", () => {
  it("merges the open description onto the child control and restores its existing descriptions", async () => {
    const user = userEvent.setup();
    render(
      <>
        <p id="existing">Existing help</p>
        <p id="additional">Additional help</p>
        <PortalTooltip content="Helpful details">
          <Button aria-describedby="existing additional">More info</Button>
        </PortalTooltip>
      </>,
    );
    const trigger = screen.getByRole("button", { name: "More info" });
    expect(screen.queryByRole("tooltip")).toBeNull();
    await user.tab();
    const tooltip = screen.getByRole("tooltip");
    expect(tooltip.parentElement).toBe(document.body);
    expect(trigger.getAttribute("aria-describedby")).toBe(`existing additional ${tooltip.id}`);
    expect(trigger.parentElement?.getAttribute("aria-describedby")).toBeNull();

    await user.tab();
    expect(screen.queryByRole("tooltip")).toBeNull();
    expect(trigger.getAttribute("aria-describedby")).toBe("existing additional");
  });

  it.each(["pointer", "focus"])("stays visible when %s leaves first", async (firstToLeave) => {
    const user = userEvent.setup();
    render(
      <PortalTooltip content="Helpful details">
        <button type="button">More info</button>
      </PortalTooltip>,
    );
    const trigger = screen.getByRole("button", { name: "More info" });
    await user.hover(trigger);
    await user.tab();
    expect(screen.getByRole("tooltip").style.visibility).not.toBe("hidden");
    if (firstToLeave === "pointer") await user.unhover(trigger);
    else await user.tab();
    expect(screen.getByRole("tooltip")).toBeTruthy();
    if (firstToLeave === "pointer") await user.tab();
    else await user.unhover(trigger);
    expect(screen.queryByRole("tooltip")).toBeNull();
    expect(trigger.getAttribute("aria-describedby")).toBeNull();
  });

  it("can show a tooltip without adding to an existing description", async () => {
    const user = userEvent.setup();
    render(
      <PortalTooltip content="Remove" describe={false}>
        <Button aria-label="Remove item" aria-describedby="existing" />
      </PortalTooltip>,
    );
    const trigger = screen.getByRole("button", { name: "Remove item" });
    await user.hover(trigger);
    await user.tab();
    expect(screen.getByRole("tooltip")).toBeTruthy();
    expect(trigger.getAttribute("aria-describedby")).toBe("existing");
    expect(trigger.parentElement?.getAttribute("aria-describedby")).toBeNull();
  });

  it("keeps the tooltip open when focus moves within its wrapper", async () => {
    const user = userEvent.setup();
    render(
      <PortalTooltip content="Helpful details" describe={false}>
        <button type="button">First</button>
        <button type="button">Second</button>
      </PortalTooltip>,
    );
    await user.tab();
    await user.tab();
    expect(document.activeElement).toBe(screen.getByRole("button", { name: "Second" }));
    expect(screen.getByRole("tooltip")).toBeTruthy();
    await user.tab();
    expect(screen.queryByRole("tooltip")).toBeNull();
  });

  it("dismisses on scroll and reopens on subsequent interaction", async () => {
    const user = userEvent.setup();
    render(
      <PortalTooltip content="Helpful details">
        <button type="button">More info</button>
      </PortalTooltip>,
    );
    const trigger = screen.getByRole("button", { name: "More info" });
    await user.hover(trigger);
    fireEvent.scroll(window);
    expect(screen.queryByRole("tooltip")).toBeNull();
    expect(trigger.getAttribute("aria-describedby")).toBeNull();
    await user.tab();
    expect(screen.getByRole("tooltip").style.visibility).not.toBe("hidden");
    await user.unhover(trigger);
    expect(screen.getByRole("tooltip")).toBeTruthy();
    await user.tab();
    expect(screen.queryByRole("tooltip")).toBeNull();
  });
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
