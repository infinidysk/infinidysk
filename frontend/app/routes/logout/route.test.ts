import { isAuthenticated, setSessionUser } from "~/auth/authentication.server";
import type { RouterContextProvider } from "react-router";
import { describe, expect, it } from "vitest";
import { action, loader } from "./route";

const testContext = {} as RouterContextProvider;
const routeArgs = (request: Request) => ({
  request,
  url: new URL(request.url),
  params: {},
  pattern: "/logout",
  context: testContext,
});

describe("logout route", () => {
  async function authenticatedRequest(method = "GET", body?: URLSearchParams) {
    const session = await setSessionUser(new Request("http://localhost/"), "admin");
    const cookie = new Headers(session.headers).get("Set-Cookie");
    if (!cookie) throw new Error("Expected a Set-Cookie header");
    const headers = { Cookie: cookie };
    return body
      ? new Request("http://localhost/logout", { method, body, headers })
      : new Request("http://localhost/logout", { method, headers });
  }

  it("preserves the session for GET requests", async () => {
    const request = await authenticatedRequest();
    const response = loader(routeArgs(request));

    expect(response).toBeNull();
    await expect(isAuthenticated(request)).resolves.toBe(true);
  });

  it("clears the session for confirmed POST requests", async () => {
    const request = await authenticatedRequest("POST", new URLSearchParams({ confirm: "true" }));
    const response = await action(routeArgs(request));

    expect(response.status).toBe(302);
    expect(response.headers.get("Location")).toBe("/login");
    await expect(
      isAuthenticated(
        new Request("http://localhost/", {
          headers: { Cookie: response.headers.get("Set-Cookie")! },
        }),
      ),
    ).resolves.toBe(false);
  });

  it("does not log out unconfirmed POST requests", async () => {
    const request = await authenticatedRequest("POST", new URLSearchParams());
    const response = await action(routeArgs(request));

    expect(response.status).toBe(302);
    expect(response.headers.get("Location")).toBe("/");
    expect(response.headers.get("Set-Cookie")).toBeNull();
    await expect(isAuthenticated(request)).resolves.toBe(true);
  });
});
