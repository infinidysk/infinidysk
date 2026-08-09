import { describe, expect, it } from "vitest";
import { action } from "./route";

describe("_index route action", () => {
  it("returns 405 for POST requests", async () => {
    const response = action();

    expect(response).toBeInstanceOf(Response);
    expect(response.status).toBe(405);
    expect(await response.text()).toBe("Method Not Allowed");
    expect(response.headers.get("Allow")).toBe("GET, HEAD");
  });
});
