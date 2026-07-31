import { beforeAll, beforeEach, describe, expect, it, vi } from "vitest";

const { authenticateMock } = vi.hoisted(() => ({
  authenticateMock: vi.fn(),
}));

vi.mock("~/clients/backend-client.server", () => ({
  backendClient: {
    authenticate: authenticateMock,
  },
}));

let authentication: typeof import("./authentication.server");

beforeAll(async () => {
  vi.stubEnv("SESSION_KEY", "test-session-key");
  vi.stubEnv("SECURE_COOKIES", "false");
  vi.stubEnv("DISABLE_FRONTEND_AUTH", "false");
  authentication = await import("./authentication.server");
});

beforeEach(() => {
  authenticateMock.mockReset();
});

function formRequest(username?: string, password?: string): Request {
  const body = new URLSearchParams();
  if (username !== undefined) body.set("username", username);
  if (password !== undefined) body.set("password", password);
  return new Request("http://localhost/login", { method: "POST", body });
}

function getSetCookie(responseInit: ResponseInit): string {
  const cookie = new Headers(responseInit.headers).get("Set-Cookie");
  if (!cookie) throw new Error("Expected a Set-Cookie header");
  return cookie;
}

describe("authentication sessions", () => {
  it("starts unauthenticated without a session cookie", async () => {
    await expect(
      authentication.isAuthenticated(new Request("http://localhost/")),
    ).resolves.toBe(false);
  });

  it("logs in valid credentials and authenticates the resulting request", async () => {
    authenticateMock.mockResolvedValueOnce(true);

    const loginResult = await authentication.login(formRequest("alice", "secret"));
    const cookie = getSetCookie(loginResult);

    expect(authenticateMock).toHaveBeenCalledWith("alice", "secret");
    const authenticatedRequest = new Request("http://localhost/", {
      headers: { Cookie: cookie },
    });
    await expect(authentication.isAuthenticated(authenticatedRequest)).resolves.toBe(true);
    await expect(authentication.getSessionUser(authenticatedRequest)).resolves.toEqual({
      username: "alice",
    });
  });

  it("returns null session user when unauthenticated", async () => {
    await expect(
      authentication.getSessionUser(new Request("http://localhost/")),
    ).resolves.toBeNull();
  });

  it("rejects missing or invalid credentials", async () => {
    await expect(authentication.login(formRequest("alice"))).rejects.toThrow(
      "username and password required",
    );

    authenticateMock.mockResolvedValueOnce(false);
    await expect(authentication.login(formRequest("alice", "wrong"))).rejects.toThrow(
      "Invalid credentials",
    );
  });

  it("sets and clears a session user", async () => {
    const setResult = await authentication.setSessionUser(
      new Request("http://localhost/"),
      "alice",
    );
    const authenticatedCookie = getSetCookie(setResult);
    const authenticatedRequest = new Request("http://localhost/", {
      headers: { Cookie: authenticatedCookie },
    });

    await expect(authentication.isAuthenticated(authenticatedRequest)).resolves.toBe(true);

    const logoutResult = await authentication.logout(authenticatedRequest);
    const loggedOutCookie = getSetCookie(logoutResult);
    await expect(authentication.isAuthenticated(new Request("http://localhost/", {
      headers: { Cookie: loggedOutCookie },
    }))).resolves.toBe(false);
  });

  it("accepts authentication cookies on Request objects", async () => {
    const setResult = await authentication.setSessionUser(
      new Request("http://localhost/"),
      "alice",
    );

    const request = new Request("http://localhost/", {
      headers: { cookie: getSetCookie(setResult) },
    });
    await expect(authentication.isAuthenticated(request)).resolves.toBe(true);
  });
});
