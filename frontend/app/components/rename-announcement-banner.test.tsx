// @vitest-environment jsdom

import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it } from "vitest";
import { RenameAnnouncementBanner } from "./rename-announcement-banner";

afterEach(() => {
  cleanup();
  localStorage.clear();
});

describe("RenameAnnouncementBanner", () => {
  it("announces the rename until dismissed", async () => {
    const user = userEvent.setup();
    render(<RenameAnnouncementBanner />);

    expect(await screen.findByText("NzbDAV is becoming InfiniDysk")).toBeTruthy();
    expect(screen.getByRole("link", { name: "Read the rename FAQ" }).getAttribute("href"))
      .toBe("https://nzbdav.com/community/renaming-to-infinidysk/");

    await user.click(screen.getByRole("button", { name: "Dismiss rename announcement" }));

    expect(screen.queryByText("NzbDAV is becoming InfiniDysk")).toBeNull();
    expect(localStorage.getItem("infinidysk-rename-announcement-v1")).toBe("dismissed");
  });

  it("stays hidden after dismissal", () => {
    localStorage.setItem("infinidysk-rename-announcement-v1", "dismissed");
    render(<RenameAnnouncementBanner />);

    expect(screen.queryByText("NzbDAV is becoming InfiniDysk")).toBeNull();
  });
});
