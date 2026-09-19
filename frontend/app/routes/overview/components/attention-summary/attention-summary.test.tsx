// @vitest-environment jsdom
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { afterEach, describe, expect, it, vi } from "vitest";
import { AttentionSummary } from "./attention-summary";
import { mockArrHealthData } from "../arr-health/arr-health.mock";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("AttentionSummary", () => {
  it("links actionable health results to Health without claiming unavailable providers are healthy", async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValue({ ok: true, json: () => Promise.resolve({ totalCount: 7 }) });
    vi.stubGlobal("fetch", fetchMock);
    render(
      <MemoryRouter>
        <AttentionSummary providers={null} arrHealth={null} hasConfiguredArrs />
      </MemoryRouter>,
    );
    expect(
      (await screen.findByRole("link", { name: /7 files need attention/ })).getAttribute("href"),
    ).toBe("/health");
    expect(screen.getByText("Provider status unavailable")).toBeTruthy();
    expect(screen.getByText("Arr status unavailable")).toBeTruthy();
    expect(fetchMock.mock.calls[0]?.[0]).toContain("currentActionNeeded=true");
  });

  it("keeps failures distinct from a successful zero result", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue({ ok: false }));
    render(
      <MemoryRouter>
        <AttentionSummary providers={[]} arrHealth={null} hasConfiguredArrs={false} />
      </MemoryRouter>,
    );
    await screen.findByText("Health status unavailable");
    expect(screen.queryByText("No files need attention")).toBeNull();
    expect(screen.getByText("No provider circuits open")).toBeTruthy();
  });

  it("surfaces degraded Arr integrations and accepts a successful empty health response", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue({ ok: true, json: () => Promise.resolve({ totalCount: 0 }) }),
    );
    const data = mockArrHealthData();
    render(
      <MemoryRouter>
        <AttentionSummary providers={[]} arrHealth={data} hasConfiguredArrs />
      </MemoryRouter>,
    );
    await waitFor(() => expect(screen.getByText("No files need attention")).toBeTruthy());
    const affected = data.instances.filter(
      (instance) => instance.status === "degraded" || instance.status === "offline",
    ).length;
    expect(screen.getByText(`${affected} Arr integrations degraded or offline`)).toBeTruthy();
  });
});
