// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { afterEach, describe, expect, it, vi } from "vitest";
import { AttentionSummary } from "./attention-summary";
import type { ArrHealthResponse } from "~/clients/backend-client.server";

function mockArrHealthData(): ArrHealthResponse {
  return {
    configured: true,
    summary: {
      instancesOnline: 1,
      instancesTotal: 1,
      importsCompleted: 0,
      medianHandoffMs: null,
      p95HandoffMs: null,
      awaitingImport: 0,
      awaitingShown: 0,
      degraded: 1,
    },
    instances: [
      {
        key: "radarr",
        name: "Home Radarr",
        appType: "radarr",
        host: "http://localhost:7878",
        status: "degraded",
        imports: 0,
        medianHandoffMs: null,
        p95HandoffMs: null,
        queueCount: 1,
        awaitingCount: 0,
        hasWarnings: true,
        hasErrors: false,
        lastImportAtMs: null,
        lastError: null,
      },
    ],
    awaiting: [],
  };
}

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe("AttentionSummary", () => {
  it.each([
    ["degraded", true, false, "This app reports queue warnings."],
    ["degraded", false, true, "This app reports queue errors."],
    ["degraded", true, true, "This app reports queue warnings and errors."],
    ["degraded", false, false, "Imports are taking longer than expected."],
    ["offline", false, false, "InfiniDysk could not poll this instance."],
  ] as const)(
    "explains %s status with warnings=%s and errors=%s in place",
    async (status, hasWarnings, hasErrors, reason) => {
      vi.stubGlobal("fetch", vi.fn().mockResolvedValue({ ok: false }));
      const data = mockArrHealthData();
      data.instances = [
        {
          ...data.instances[0]!,
          name: "Home Radarr",
          status,
          hasWarnings,
          hasErrors,
          queueCount: 1,
          awaitingCount: 0,
          lastError: status === "offline" ? "Connection refused" : null,
        },
      ];
      data.awaiting = [];
      render(
        <MemoryRouter>
          <AttentionSummary providers={[]} arrHealth={data} hasConfiguredArrs />
        </MemoryRouter>,
      );
      fireEvent.click(screen.getByLabelText(/Alerts:/));
      await screen.findByText("Health status unavailable");
      expect(screen.getByText("1 Arr integration degraded or offline")).toBeTruthy();
      expect(screen.getByText("Home Radarr", { exact: false })).toBeTruthy();
      expect(screen.getByText(reason, { exact: false })).toBeTruthy();
      expect(
        screen.getByRole("link", { name: "Arr connection settings" }).getAttribute("href"),
      ).toBe("/settings?tab=arrs");
      if (status === "offline") expect(screen.getByText("Connection refused")).toBeTruthy();
    },
  );

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
    fireEvent.click(screen.getByLabelText(/Alerts:/));
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
    expect(screen.queryByText("No provider circuits open")).toBeNull();
    expect(screen.queryByText("No issues need attention")).toBeNull();
  });

  it("times out a health request without preventing a later visibility refresh", async () => {
    vi.useFakeTimers();
    const fetchMock = vi
      .fn()
      .mockImplementationOnce(
        (_url, init?: RequestInit) =>
          new Promise((_, reject) => {
            init?.signal?.addEventListener("abort", () => reject(new DOMException("Aborted")));
          }),
      )
      .mockResolvedValueOnce({ ok: true, json: () => Promise.resolve({ totalCount: 0 }) });
    vi.stubGlobal("fetch", fetchMock);
    render(
      <MemoryRouter>
        <AttentionSummary providers={[]} arrHealth={null} hasConfiguredArrs={false} />
      </MemoryRouter>,
    );

    await act(async () => {
      await vi.advanceTimersByTimeAsync(5_000);
    });
    expect(screen.getByText("Health status unavailable")).toBeTruthy();

    await act(async () => {
      document.dispatchEvent(new Event("visibilitychange"));
      await vi.advanceTimersByTimeAsync(0);
    });
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(screen.getByText("No issues need attention")).toBeTruthy();
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
    await waitFor(() => expect(screen.queryByText("Checking status...")).toBeNull());
    expect(screen.queryByText("No issues need attention")).toBeNull();
    const affected = data.instances.filter(
      (instance) => instance.status === "degraded" || instance.status === "offline",
    ).length;
    expect(screen.getByText(`${affected} Arr integration degraded or offline`)).toBeTruthy();
  });

  it("stops pulsing when opened, closes on Escape, and keeps unresolved alerts yellow", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue({ ok: true, json: () => Promise.resolve({ totalCount: 7 }) }),
    );
    render(
      <MemoryRouter>
        <AttentionSummary providers={[]} arrHealth={null} hasConfiguredArrs={false} />
      </MemoryRouter>,
    );
    await screen.findByText("7 files need attention");
    const trigger = screen.getByLabelText("Alerts: needs attention");
    expect(trigger.querySelector("span[class*='animate-']")).not.toBeNull();
    fireEvent.click(trigger);
    await waitFor(() => expect(trigger.querySelector("span[class*='animate-']")).toBeNull());
    fireEvent.keyDown(trigger, { key: "Escape" });
    expect(trigger.closest("details")?.open).toBe(false);
    expect(document.activeElement).toBe(trigger);
    expect(trigger.className).toContain("text-warning");
  });

  it("escalates at three consecutive trips with the provider name, reason, and recovery action", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue({ ok: true, json: () => Promise.resolve({ totalCount: 0 }) }),
    );
    const provider = {
      provider: "news.example.com",
      nickname: "Backup account",
      circuitState: "open" as const,
      lastFailureReason: "Connection refused",
      consecutiveTrips: 2,
    };
    const view = render(
      <MemoryRouter>
        <AttentionSummary providers={[provider]} arrHealth={null} hasConfiguredArrs={false} />
      </MemoryRouter>,
    );
    await screen.findByText("1 provider circuit open or recovering");
    expect(screen.queryByText("Backup account unreachable")).toBeNull();

    fireEvent.click(screen.getByLabelText("Alerts: needs attention"));
    await waitFor(() =>
      expect(screen.getByLabelText("Alerts: needs attention").querySelector("span[class*='animate-']"))
        .toBeNull(),
    );
    fireEvent.click(screen.getByLabelText("Alerts: needs attention"));
    await waitFor(() =>
      expect(screen.getByLabelText("Alerts: needs attention").closest("details")?.open).toBe(false),
    );
    view.rerender(
      <MemoryRouter>
        <AttentionSummary
          providers={[{ ...provider, consecutiveTrips: 3 }]}
          arrHealth={null}
          hasConfiguredArrs={false}
        />
      </MemoryRouter>,
    );

    expect(screen.queryByText("1 provider circuit open or recovering")).toBeNull();
    expect(screen.getByText("Backup account unreachable")).toBeTruthy();
    expect(screen.getByText("Connection refused")).toBeTruthy();
    expect(screen.getByText(/Missing-article checks are paused/)).toBeTruthy();
    expect(screen.getByRole("link", { name: "Disable or fix it in Usenet settings" })
      .getAttribute("href")).toBe("/settings");
    expect(screen.getByLabelText("Alerts: needs attention").querySelector("span[class*='animate-']"))
      .not.toBeNull();
  });

  it("does not escalate when provider status is unavailable", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue({ ok: false }));
    render(
      <MemoryRouter>
        <AttentionSummary providers={null} arrHealth={null} hasConfiguredArrs={false} />
      </MemoryRouter>,
    );

    await screen.findByText("Provider status unavailable");
    expect(screen.queryByText(/repeatedly open/)).toBeNull();
    expect(screen.queryByRole("link", { name: "Disable or fix it in Usenet settings" })).toBeNull();
  });
});
