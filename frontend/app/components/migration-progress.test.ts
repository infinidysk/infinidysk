import { describe, expect, it } from "vitest";
import {
  clearMigrationObserved,
  clearReloadAttempts,
  decideMigrationStatusPoll,
  MAX_RELOAD_ATTEMPTS,
  readMigrationObserved,
  readReloadAttempts,
  writeMigrationObserved,
  writeReloadAttempts,
} from "./migration-progress";

function createMemoryStorage(): Storage {
  const store = new Map<string, string>();
  return {
    get length() {
      return store.size;
    },
    clear() {
      store.clear();
    },
    getItem(key: string) {
      return store.has(key) ? store.get(key)! : null;
    },
    key(index: number) {
      return Array.from(store.keys())[index] ?? null;
    },
    removeItem(key: string) {
      store.delete(key);
    },
    setItem(key: string, value: string) {
      store.set(key, value);
    },
  };
}

describe("decideMigrationStatusPoll", () => {
  it("keeps polling while migration JSON is running", () => {
    const status = {
      state: "running" as const,
      startedAt: 1,
      completed: 0,
      total: 1,
      currentStep: "Metrics database",
      error: null,
      steps: [],
    };

    expect(decideMigrationStatusPoll(200, status)).toEqual({
      action: "migrating",
      status,
      reloadMs: undefined,
    });
  });

  it("reloads when migration completes", () => {
    const status = {
      state: "completed" as const,
      startedAt: 1,
      completed: 1,
      total: 1,
      currentStep: null,
      error: null,
      steps: [],
    };

    expect(decideMigrationStatusPoll(200, status)).toEqual({
      action: "migrating",
      status,
      reloadMs: 1500,
    });
  });

  it("treats 404 as backend handoff complete", () => {
    expect(decideMigrationStatusPoll(404, null)).toEqual({
      action: "connecting",
      reloadMs: 1500,
    });
  });

  it("keeps reloading on 404 until the attempt cap is reached", () => {
    expect(decideMigrationStatusPoll(404, null, MAX_RELOAD_ATTEMPTS - 1)).toEqual({
      action: "connecting",
      reloadMs: 1500,
    });
    expect(decideMigrationStatusPoll(404, null, MAX_RELOAD_ATTEMPTS)).toEqual({
      action: "fallback",
      stopPolling: true,
    });
  });

  it("keeps reloading on 404 after migration has been observed", () => {
    expect(decideMigrationStatusPoll(404, null, MAX_RELOAD_ATTEMPTS, true)).toEqual({
      action: "connecting",
      reloadMs: 1500,
    });
  });

  it("treats 502/503 as connecting with a longer reload", () => {
    expect(decideMigrationStatusPoll(502, null)).toEqual({
      action: "connecting",
      reloadMs: 5000,
    });
    expect(decideMigrationStatusPoll(503, null)).toEqual({
      action: "connecting",
      reloadMs: 5000,
    });
  });

  it("falls back and stops for unexpected responses", () => {
    expect(decideMigrationStatusPoll(200, { hello: "world" })).toEqual({
      action: "fallback",
      stopPolling: true,
    });
    expect(decideMigrationStatusPoll(500, null)).toEqual({
      action: "fallback",
      stopPolling: true,
    });
  });
});

describe("reload attempt storage", () => {
  it("returns 0 when storage is empty or corrupt", () => {
    const storage = createMemoryStorage();
    expect(readReloadAttempts(storage, 1_000)).toBe(0);

    storage.setItem("infinidysk.migration-reload-attempts", "{not-json");
    expect(readReloadAttempts(storage, 1_000)).toBe(0);

    storage.setItem("infinidysk.migration-reload-attempts", JSON.stringify({ count: "bad", lastAt: 1 }));
    expect(readReloadAttempts(storage, 1_000)).toBe(0);
  });

  it("reads and writes reload attempts", () => {
    const storage = createMemoryStorage();
    const now = 10_000;

    writeReloadAttempts(storage, 2, now);
    expect(readReloadAttempts(storage, now)).toBe(2);
    expect(readReloadAttempts(storage, now + 30_000)).toBe(2);
  });

  it("expires stale reload attempts after the reset window", () => {
    const storage = createMemoryStorage();
    const now = 10_000;

    writeReloadAttempts(storage, 2, now);
    expect(readReloadAttempts(storage, now + 2 * 60 * 1000 + 1)).toBe(0);
  });

  it("clears stored reload attempts", () => {
    const storage = createMemoryStorage();
    const now = 10_000;

    writeReloadAttempts(storage, 2, now);
    clearReloadAttempts(storage);
    expect(readReloadAttempts(storage, now)).toBe(0);
  });

  it("persists migration observed across reloads", () => {
    const storage = createMemoryStorage();

    expect(readMigrationObserved(storage)).toBe(false);
    writeMigrationObserved(storage);
    expect(readMigrationObserved(storage)).toBe(true);
    clearMigrationObserved(storage);
    expect(readMigrationObserved(storage)).toBe(false);
  });
});
