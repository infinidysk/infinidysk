// @vitest-environment jsdom
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import { EmptyQueue } from "./empty-queue";

afterEach(cleanup);

describe("EmptyQueue", () => {
  it("offers to clear filters when filters hide every job", async () => {
    const onClearFilters = vi.fn();
    render(<EmptyQueue onClearFilters={onClearFilters} />);

    expect(screen.getByRole("heading", { name: "No jobs match your filters" })).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: /Clear filters/ }));
    expect(onClearFilters).toHaveBeenCalledOnce();
  });

  it("explains how to add jobs when the queue is truly empty", () => {
    render(<EmptyQueue />);

    expect(screen.getByRole("heading", { name: "Nothing in the queue" })).toBeTruthy();
    expect(screen.queryByRole("button")).toBeNull();
  });
});
