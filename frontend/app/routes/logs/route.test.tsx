// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Route } from "./+types/route";
import Logs from "./route";

vi.mock("~/clients/backend-client.server", () => ({ backendClient: {} }));
vi.mock("./controllers/websocket-controller", () => ({ useLogsWebsocket: vi.fn() }));

const fetchMock = vi.fn<(input: string) => Promise<Response>>();

function renderLogs() {
  const loaderData = {
    entries: [],
    countsByLevel: { Information: 3 },
    capacity: 2000,
  } as unknown as Route.ComponentProps["loaderData"];
  return render(<Logs {...({ loaderData } as Route.ComponentProps)} />);
}

describe("Logs", () => {
  beforeEach(() => {
    vi.stubGlobal("fetch", fetchMock);
    fetchMock.mockResolvedValue(
      new Response(JSON.stringify({ entries: [], countsByLevel: { Information: 3 } })),
    );
    window.history.replaceState(null, "", "/logs?q=nomatch");
  });

  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
    fetchMock.mockReset();
  });

  it("offers clear filters in the empty state and resets them", async () => {
    renderLogs();
    expect(screen.getByRole("heading", { name: "No entries match your filters" })).toBeTruthy();

    fireEvent.click(screen.getAllByRole("button", { name: /clear filters/i })[0]!);

    expect(screen.getByRole<HTMLInputElement>("searchbox").value).toBe("");
    await waitFor(() =>
      expect(screen.queryByRole("button", { name: /clear filters/i })).toBeNull(),
    );
  });
});
