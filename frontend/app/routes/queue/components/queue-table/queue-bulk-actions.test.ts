import { describe, expect, it } from "vitest";
import {
  buildClearQueueUrl,
  buildQueuePauseResumeUrl,
  canPauseQueueSlot,
  canResumeQueueSlot,
} from "./queue-bulk-actions";

describe("queue-bulk-actions", () => {
  it("canPauseQueueSlot excludes paused and uploading rows", () => {
    expect(canPauseQueueSlot({ status: "Queued" })).toBe(true);
    expect(canPauseQueueSlot({ status: "Paused" })).toBe(false);
    expect(canPauseQueueSlot({ status: "Queued", isUploading: true })).toBe(false);
  });

  it("canResumeQueueSlot only allows paused rows", () => {
    expect(canResumeQueueSlot({ status: "Paused" })).toBe(true);
    expect(canResumeQueueSlot({ status: "Queued" })).toBe(false);
  });

  it("buildClearQueueUrl adds category when provided", () => {
    expect(buildClearQueueUrl()).toBe("/api?mode=queue&name=delete&value=all");
    expect(buildClearQueueUrl("tv")).toBe("/api?mode=queue&name=delete&value=all&cat=tv");
  });

  it("buildQueuePauseResumeUrl uses queue name aliases", () => {
    expect(buildQueuePauseResumeUrl("pause")).toBe("/api?mode=queue&name=pause");
  });
});
