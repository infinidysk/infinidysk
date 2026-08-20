// @vitest-environment jsdom
/* global HTMLSelectElement */
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { ReactNode, SelectHTMLAttributes } from "react";

vi.mock("~/components/ui", () => ({
  Select: ({
    children,
    ...props
  }: SelectHTMLAttributes<HTMLSelectElement> & { children?: ReactNode }) => (
    <select {...props}>{children}</select>
  ),
}));

import { SimpleDropdown } from "./simple-dropdown";

afterEach(cleanup);

describe("SimpleDropdown", () => {
  it("displays valueRef.current on first render when it differs from options[0]", () => {
    const valueRef = { current: "anime" };

    render(
      <SimpleDropdown
        options={["tv", "movies", "anime"]}
        valueRef={valueRef}
        ariaLabel="Upload category"
      />,
    );

    expect(screen.getByRole<HTMLSelectElement>("combobox", { name: "Upload category" }).value).toBe(
      "anime",
    );
  });

  it("writes the selected option through valueRef and updates the display", async () => {
    const user = userEvent.setup();
    const valueRef = { current: "tv" };

    render(
      <SimpleDropdown
        options={["tv", "movies", "anime"]}
        valueRef={valueRef}
        ariaLabel="Upload category"
      />,
    );

    const select = screen.getByRole<HTMLSelectElement>("combobox", { name: "Upload category" });
    await user.selectOptions(select, "movies");

    expect(valueRef.current).toBe("movies");
    expect(select.value).toBe("movies");
  });

  it("renders the provided value and calls onChange in value/onChange mode", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();

    render(
      <SimpleDropdown
        options={["tv", "movies", "anime"]}
        value="movies"
        onChange={onChange}
        ariaLabel="Bulk category"
      />,
    );

    const select = screen.getByRole<HTMLSelectElement>("combobox", { name: "Bulk category" });
    expect(select.value).toBe("movies");

    await user.selectOptions(select, "anime");

    expect(onChange).toHaveBeenCalledWith("anime");
    expect(select.value).toBe("movies");
  });
});
