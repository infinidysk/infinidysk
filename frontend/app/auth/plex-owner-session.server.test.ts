import { afterEach, expect, it, vi } from "vitest";

vi.mock("~/clients/backend-client.server", () => ({ backendClient: {} }));
afterEach(() => { vi.unstubAllEnvs(); vi.resetModules(); });

it("isolates Plex handles by signed browser session when frontend auth is explicitly disabled", async () => {
  vi.stubEnv("SESSION_KEY", "plex-owner-test");
  vi.stubEnv("SECURE_COOKIES", "false");
  vi.stubEnv("DISABLE_FRONTEND_AUTH", "true");
  const auth = await import("./authentication.server");
  const first = await auth.ensurePlexOwnerSession(new Request("http://localhost/api/plex/login/start"));
  const second = await auth.ensurePlexOwnerSession(new Request("http://localhost/api/plex/login/start"));
  expect(first.owner).toBeTruthy();
  expect(first.owner).not.toBe(second.owner);
  expect(first.cookie).toContain("HttpOnly");
  const same = await auth.ensurePlexOwnerSession(new Request("http://localhost/api/plex/login/poll", {
    headers: { Cookie: first.cookie!.split(";", 1)[0]! },
  }));
  expect(same.owner).toBe(first.owner);
  expect(same.cookie).toBeUndefined();
});

it("does not mint Plex ownership for unauthenticated browsers", async () => {
  vi.stubEnv("SESSION_KEY", "plex-owner-test");
  vi.stubEnv("SECURE_COOKIES", "false");
  vi.stubEnv("DISABLE_FRONTEND_AUTH", "false");
  const auth = await import("./authentication.server");
  expect(await auth.ensurePlexOwnerSession(new Request("http://localhost/api/plex/login/start"))).toEqual({ owner: null });
});
