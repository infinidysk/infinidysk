import { describe, expect, it } from "vitest";
import {
  describeTraceEvent,
  redactTraceEvents,
  summarizeTrace,
  type StreamTraceEvent,
} from "./media-trace";

function event(partial: Partial<StreamTraceEvent> & { kind: string }): StreamTraceEvent {
  return { seq: 1, at: 1_000, ...partial };
}

describe("summarizeTrace", () => {
  it("counts each event kind and tracks the last range end", () => {
    const events = [
      event({ kind: "RangeOpen", rangeStart: 0, rangeEnd: 999 }),
      event({ kind: "Segment", provider: "a", status: "Ok" }),
      event({ kind: "Segment", provider: "b", status: "Ok" }),
      event({ kind: "Segment", provider: "a", status: "NotFound" }),
      event({ kind: "Seek", offset: 500 }),
      event({ kind: "Retry", attempt: 2 }),
      event({ kind: "Failover", fromProvider: "a", toProvider: "b" }),
      event({ kind: "ZeroFill", bytes: 64 }),
      event({ kind: "PrefetchWidth", previousBatchSize: 4, batchSize: 2 }),
      event({
        kind: "RangeEnd",
        endReason: "Completed",
        bytesServed: 1000,
        connWaitMs: 5,
        providerWaitMs: 50,
        bodyDrainMs: 10,
        consumerWaitMs: 20,
        clientWriteMs: 1,
      }),
    ];

    const summary = summarizeTrace(events);
    expect(summary.rangeOpens).toBe(1);
    expect(summary.rangeEnds).toBe(1);
    expect(summary.seeks).toBe(1);
    expect(summary.segments).toBe(3);
    expect(summary.segmentsByStatus).toEqual({ Ok: 2, NotFound: 1 });
    expect(summary.retries).toBe(1);
    expect(summary.failovers).toBe(1);
    expect(summary.zeroFills).toBe(1);
    expect(summary.prefetchChanges).toBe(1);
    expect(summary.bytesServed).toBe(1000);
    expect(summary.lastEndReason).toBe("Completed");
    expect(summary.stallTotalsMs).toEqual({
      connWait: 5,
      providerWait: 50,
      bodyDrain: 10,
      consumerWait: 20,
      clientWrite: 1,
    });
  });

  it("returns zeros for an empty trace", () => {
    const summary = summarizeTrace([]);
    expect(summary.segments).toBe(0);
    expect(summary.lastEndReason).toBeNull();
    expect(summary.stallTotalsMs).toBeNull();
  });
});

describe("redactTraceEvents", () => {
  it("truncates segment ids and leaves other events untouched", () => {
    const longId = "abcdefghijklmnopqrstuvwxyz@provider";
    const [redacted] = redactTraceEvents([event({ kind: "Segment", segmentId: longId })]);
    expect(redacted!.segmentId).toBe("abcdefghijkl…");
    expect(redacted!.segmentId).not.toContain("provider");

    const [untouched] = redactTraceEvents([event({ kind: "Seek", offset: 5 })]);
    expect(untouched!.segmentId).toBeUndefined();
  });
});

describe("describeTraceEvent", () => {
  it("renders human-readable lines per kind", () => {
    expect(describeTraceEvent(event({ kind: "RangeOpen", rangeStart: 0, rangeEnd: 99 }))).toBe(
      "range 0–99",
    );
    expect(
      describeTraceEvent(event({ kind: "Failover", fromProvider: "a", toProvider: "b" })),
    ).toBe("a → b");
    expect(
      describeTraceEvent(event({ kind: "PrefetchWidth", previousBatchSize: 4, batchSize: 2 })),
    ).toBe("4 → 2");
    expect(
      describeTraceEvent(
        event({
          kind: "FirstByte",
          status: "exact-index-direct",
          bytes: 512,
          durationMs: 7,
        }),
      ),
    ).toBe("exact-index-direct · 512 B · 7 ms");
    expect(
      describeTraceEvent(event({ kind: "RangeEnd", endReason: "Aborted", bytesServed: 512 })),
    ).toBe("Aborted · 512 B");
  });
});
