// @vitest-environment jsdom

import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { ResetAdminPasswordBanner } from "./reset-admin-password-banner";

afterEach(() => {
  cleanup();
});

describe("ResetAdminPasswordBanner", () => {
  it("warns when RESET_ADMIN_PASSWORD is set", () => {
    render(<ResetAdminPasswordBanner isResetAdminPasswordSet={true} />);

    expect(screen.getByText("Admin password reset is armed")).toBeTruthy();
    expect(screen.getByText(/RESET_ADMIN_PASSWORD/)).toBeTruthy();
    expect(screen.queryByRole("button")).toBeNull();
  });

  it("renders nothing when the env flag is unset", () => {
    const { container } = render(<ResetAdminPasswordBanner isResetAdminPasswordSet={false} />);

    expect(container.innerHTML).toBe("");
  });
});
