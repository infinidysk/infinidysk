// @vitest-environment jsdom
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { PlexMedia, PlexSource, PlexUser } from "../plex/plex-api";
import { parsePrefetchSettings } from "./smart-prefetch-model";
import type { PlexCatalogueLibrary } from "./plex-source-catalogue";
import { PlexMediaSection } from "./plex-media-section";
import { PlexSourceCustomization } from "./plex-source-customization";
import { PlexSourceToolbar } from "./plex-source-toolbar";

afterEach(cleanup);

function source(overrides: Partial<PlexSource> = {}): PlexSource {
  return {
    serverId: "server",
    libraryId: "2",
    kind: "hub",
    id: "home.onDeck",
    key: "/hubs/on-deck",
    title: "On Deck",
    type: "show",
    ...overrides,
  };
}

function library(): PlexCatalogueLibrary {
  return {
    identity: { serverId: "server", libraryId: "2", type: "show" },
    title: "TV",
    hubs: [source()],
    collections: [
      source({
        kind: "collection",
        id: "anime",
        key: "/library/collections/anime/children",
        title: "Anime",
        type: "collection",
      }),
    ],
    unavailable: [],
    configured: false,
    enabled: false,
  };
}

describe("Plex source presentation", () => {
  it("renders accessible library and nested collection accordions", async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    render(
      <PlexMediaSection
        type="show"
        title="TV shows"
        enabled
        libraries={[library()]}
        settings={parsePrefetchSettings(undefined)}
        onChange={onChange}
        onCustomize={vi.fn()}
      />,
    );

    expect(screen.getByLabelText("Enable TV shows prefetch")).toBeTruthy();
    expect(screen.getByLabelText("Enable TV shows library TV")).toBeTruthy();
    const libraryButton = screen.getByRole("button", { name: "Show TV sources" });
    expect(libraryButton.getAttribute("aria-expanded")).toBe("false");
    expect(libraryButton.getAttribute("aria-controls")).toBeTruthy();
    await user.click(libraryButton);
    expect(libraryButton.getAttribute("aria-expanded")).toBe("true");
    expect(screen.getByLabelText("Enable TV source On Deck")).toBeTruthy();

    const collections = screen.getByRole("button", { name: "Show TV collections" });
    expect(collections.getAttribute("aria-expanded")).toBe("false");
    await user.click(collections);
    expect(screen.getByLabelText("Enable TV collection Anime")).toBeTruthy();
    expect(
      screen.getByLabelText("Enable TV collection Anime").closest("label")?.className,
    ).toContain("min-h-11");
  });

  it("shows customization only for configured sources", async () => {
    const user = userEvent.setup();
    const onCustomize = vi.fn();
    const settings = {
      ...parsePrefetchSettings(undefined),
      Sources: [
        {
          ServerId: "server",
          LibraryId: "2",
          Kind: "hub",
          Key: "/hubs/on-deck",
          Title: "On Deck",
          Type: "show",
          Enabled: true,
          Limit: 10,
          ExcludedShows: [],
        },
      ],
    };
    render(
      <PlexMediaSection
        type="show"
        title="TV shows"
        enabled
        libraries={[{ ...library(), configured: true, enabled: true }]}
        settings={settings}
        onChange={vi.fn()}
        onCustomize={onCustomize}
      />,
    );

    await user.click(screen.getByRole("button", { name: "Show TV sources" }));
    await user.click(screen.getByRole("button", { name: "Customize On Deck" }));
    expect(onCustomize).toHaveBeenCalledWith(settings.Sources[0]);
  });

  it("explains when an expanded library has no compatible Plex sources", async () => {
    const user = userEvent.setup();
    render(
      <PlexMediaSection
        type="show"
        title="TV shows"
        enabled
        libraries={[{ ...library(), hubs: [], collections: [] }]}
        settings={parsePrefetchSettings(undefined)}
        onChange={vi.fn()}
        onCustomize={vi.fn()}
      />,
    );

    await user.click(screen.getByRole("button", { name: "Show TV sources" }));
    expect(screen.getByText("No compatible hubs or collections returned by Plex.")).toBeTruthy();
  });

  it("renders source limits, TV exclusions, preview, and mapping feedback", async () => {
    const user = userEvent.setup();
    const configured = {
      ServerId: "server",
      LibraryId: "2",
      Kind: "hub",
      Key: "/hubs/on-deck",
      Title: "On Deck",
      Type: "show",
      Enabled: true,
      Limit: 10,
      ExcludedShows: ["42"],
    };
    const preview: PlexMedia[] = [
      {
        ratingKey: "7",
        title: "Episode one",
        type: "episode",
        file: "/Plex/episode.mkv",
        mappingStatus: "unmapped",
        mappingReason: "No exact mapping",
      },
    ];
    const onPreview = vi.fn();
    render(
      <PlexSourceCustomization
        source={configured}
        preview={preview}
        busy={false}
        onChange={vi.fn()}
        onPreview={onPreview}
        onClose={vi.fn()}
      />,
    );

    expect(screen.getByLabelText("Item limit for On Deck")).toBeTruthy();
    expect(document.activeElement).toBe(screen.getByLabelText("Item limit for On Deck"));
    expect(screen.getByLabelText("Excluded shows for On Deck")).toBeTruthy();
    await user.click(screen.getByRole("button", { name: "Preview On Deck" }));
    expect(onPreview).toHaveBeenCalledOnce();
    expect(screen.getByText("Episode one")).toBeTruthy();
    expect(screen.getByText(/unmapped.*No exact mapping/i)).toBeTruthy();
  });

  it("offers a simple watching profile and refresh toolbar", async () => {
    const user = userEvent.setup();
    const users: PlexUser[] = [
      { id: "owner", name: "Owner" },
      { id: "child", name: "Child" },
    ];
    const onUsersChange = vi.fn();
    const onRefresh = vi.fn();
    render(
      <PlexSourceToolbar
        servers={[
          { id: "server", name: "Home", url: "", token: "", enabled: true, pathMappings: [] },
        ]}
        serverId="server"
        users={users}
        selectedUsers={[]}
        busy={false}
        lastSuccess="2026-09-21T00:00:00Z"
        stale={false}
        onServerChange={vi.fn()}
        onUsersChange={onUsersChange}
        onRefresh={onRefresh}
      />,
    );

    await user.selectOptions(screen.getByLabelText("Watching profile"), "server:child");
    expect(onUsersChange).toHaveBeenCalledWith(["server:child"]);
    await user.click(screen.getByRole("button", { name: "Refresh Plex catalogue" }));
    expect(onRefresh).toHaveBeenCalledOnce();
  });
});
