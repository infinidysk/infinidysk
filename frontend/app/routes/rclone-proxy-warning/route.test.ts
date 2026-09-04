import { beforeEach, describe, expect, it } from "vitest";
import {
  recordRcloneProxyRequest,
  resetRcloneProxyWarningForTests,
} from "../../../server/rclone-proxy-warning.server";
import { loader } from "./route";

describe("rclone proxy warning status route", () => {
  beforeEach(() => {
    resetRcloneProxyWarningForTests();
  });

  it("returns the shared frontend detection state", async () => {
    expect(await loader().json()).toEqual({ active: false });

    recordRcloneProxyRequest("rclone/v1.70.3");

    expect(await loader().json()).toEqual({ active: true });
  });
});
