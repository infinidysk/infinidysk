// @vitest-environment jsdom
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { DropdownOptions } from "./dropdown-options";

afterEach(cleanup);

describe("DropdownOptions", () => {
  it("closes the menu when a button option is selected", async () => {
    const onSelect = vi.fn();
    const onClose = vi.fn();
    render(<DropdownOptions options={[{ option: "Delete", onSelect }]} onClose={onClose} />);

    await userEvent.click(screen.getByRole("button", { name: "Delete" }));

    expect(onSelect).toHaveBeenCalledOnce();
    expect(onClose).toHaveBeenCalledOnce();
  });

  it("closes the menu when a link option is selected", async () => {
    const onSelect = vi.fn();
    const onClose = vi.fn();
    render(
      <DropdownOptions
        options={[{ option: "Download", linkTo: "#download", onSelect }]}
        onClose={onClose}
      />,
    );

    await userEvent.click(screen.getByRole("link", { name: "Download" }));

    expect(onSelect).toHaveBeenCalledOnce();
    expect(onClose).toHaveBeenCalledOnce();
  });

  it("does not swallow the outside click that closes it", async () => {
    const onClose = vi.fn();
    render(
      <div>
        <input type="checkbox" aria-label="row selection" />
        <DropdownOptions options={[{ option: "Rename" }]} onClose={onClose} />
      </div>,
    );

    const checkbox = screen.getByRole<HTMLInputElement>("checkbox", {
      name: "row selection",
    });
    await userEvent.click(checkbox);

    // The outside click both closes the menu and still lands on its target.
    // Before the fix, the document-level handler called preventDefault, so
    // closing the menu left the clicked checkbox untoggled.
    expect(onClose).toHaveBeenCalledOnce();
    expect(checkbox.checked).toBe(true);
  });

  it("does not close from clicks landing inside the menu itself", async () => {
    const onClose = vi.fn();
    render(<DropdownOptions options={[{ option: "Rename" }]} onClose={onClose} />);

    await userEvent.click(screen.getByRole("list"));

    expect(onClose).not.toHaveBeenCalled();
  });
});
