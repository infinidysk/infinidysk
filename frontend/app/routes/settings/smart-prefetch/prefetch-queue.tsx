import { useEffect, useRef, useState } from "react";
import { Alert, Button, Input, SettingsCard, Textarea } from "~/components/ui";
import { withUrlBase } from "~/utils/url-base";

type Job = {
  id: string;
  itemId: string;
  trigger: string;
  priority: number;
  state: string;
  start: number;
  length: number;
  committedBytes: number;
  error: string | null;
  displayName?: string;
  source?: string;
  reason?: string;
  fileSize?: number;
};
type QueueStatus = {
  available: boolean;
  healthy?: boolean;
  runtimeError?: string;
  paused: boolean;
  initializationError: string | null;
  jobs: Job[];
  lastSuccess?: string | null;
  lastError?: string | null;
};
type Prediction = {
  eligible?: boolean;
  itemId: string;
  displayName: string;
  source: string;
  reason: string;
  start: number;
  length: number;
  fileSize: number;
};
type ItemOutcome = {
  itemId: string;
  status: string;
  jobId?: string;
  start: number;
  length: number;
  reason?: string;
};
export function PrefetchQueue() {
  const [status, setStatus] = useState<QueueStatus | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState("");
  const [outcomes, setOutcomes] = useState<ItemOutcome[]>([]);
  const [busy, setBusy] = useState(false);
  const [ids, setIds] = useState("");
  const [start, setStart] = useState(0);
  const [length, setLength] = useState(0);
  const [priorities, setPriorities] = useState<Record<string, number>>({});
  const [page, setPage] = useState(0);
  const [filter, setFilter] = useState("all");
  const [predictions, setPredictions] = useState<Prediction[] | null>(null);
  const [previewBusy, setPreviewBusy] = useState(false);
  const [previewWarning, setPreviewWarning] = useState<string | null>(null);
  const previewRequest = useRef<AbortController | null>(null);
  useEffect(() => {
    const pending = previewRequest;
    return () => pending.current?.abort();
  }, []);
  const previewPolicies = async () => {
    const abort = new AbortController();
    previewRequest.current?.abort();
    previewRequest.current = abort;
    setPreviewBusy(true);
    setError(null);
    setPreviewWarning(null);
    setMessage("");
    const timeout = setTimeout(() => abort.abort(), 20000);
    try {
      const response = await fetch(withUrlBase("/api/prefetch/preview"), { signal: abort.signal });
      if (!response.ok)
        throw new Error("Policy preview unavailable. Check the saved source configuration.");
      const result = (await response.json()) as { predictions: Prediction[]; warning?: string };
      if (!abort.signal.aborted) {
        setPredictions(result.predictions.slice(0, 100));
        setPreviewWarning(result.warning ?? null);
      }
    } catch (cause) {
      if (!abort.signal.aborted)
        setError(cause instanceof Error ? cause.message : "Policy preview failed.");
      else setMessage("Policy preview cancelled or timed out. No warming jobs were created.");
    } finally {
      clearTimeout(timeout);
      if (previewRequest.current === abort) {
        setPreviewBusy(false);
        previewRequest.current = null;
      }
    }
  };
  const refresh = async (signal?: AbortSignal) => {
    const response = await fetch(withUrlBase("/api/prefetch"), signal ? { signal } : {});
    if (!response.ok) throw new Error("Warming queue is unavailable.");
    const result = (await response.json()) as QueueStatus;
    if (!signal?.aborted) setStatus(result);
  };
  useEffect(() => {
    const abort = new AbortController();
    const run = () =>
      void refresh(abort.signal).catch((cause) => {
        if (!abort.signal.aborted)
          setError(cause instanceof Error ? cause.message : "Queue refresh failed.");
      });
    run();
    const timer = setInterval(run, 10000);
    return () => {
      abort.abort();
      clearInterval(timer);
    };
  }, []);
  const operate = async (operation: string, fields: object = {}) => {
    setBusy(true);
    setError(null);
    setMessage("");
    setOutcomes([]);
    try {
      const response = await fetch(withUrlBase("/api/prefetch/operations"), {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ operation, ...fields }),
      });
      if (!response.ok)
        throw new Error(
          "Warming operation rejected. Check Native cache readiness, item IDs, budgets, and job state.",
        );
      const result = (await response.json()) as { rejected?: string[]; outcomes?: ItemOutcome[] };
      setOutcomes(result.outcomes?.slice(0, 32) ?? []);
      setMessage(
        result.rejected?.length
          ? `${result.rejected.length} item(s) were rejected; accepted items remain in the queue.`
          : "Operation accepted; progress below counts committed cache bytes.",
      );
      await refresh();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Warming operation failed.");
    } finally {
      setBusy(false);
    }
  };
  const selected = [...new Set(ids.split(/[\s,;]+/).filter(Boolean))];
  const valid =
    selected.length > 0 &&
    selected.length <= 32 &&
    selected.every((id) =>
      /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(id),
    ) &&
    Number.isSafeInteger(start) &&
    start >= 0 &&
    Number.isSafeInteger(length) &&
    length >= 0;
  const jobs = (status?.jobs ?? []).filter((job) => filter === "all" || job.state === filter);
  return (
    <SettingsCard
      icon="playlist_play"
      title="Native warming queue"
      description="One bounded background queue shares existing NNTP admission. Native cache must be active with a healthy writable folder. Manual warming does not require Plex."
    >
      {error && <Alert variant="danger">{error}</Alert>}
      {previewWarning && <Alert variant="warning">{previewWarning}</Alert>}
      {status?.initializationError && <Alert variant="warning">{status.initializationError}</Alert>}
      {status?.runtimeError && <Alert variant="warning">{status.runtimeError}</Alert>}
      {status?.lastError && <Alert variant="warning">{status.lastError}</Alert>}
      <p className="text-xs">
        Last successful policy refresh:{" "}
        {status?.lastSuccess ? new Date(status.lastSuccess).toLocaleString() : "not yet completed"}.
      </p>
      {message && <p role="status">{message}</p>}
      {outcomes.length > 0 && (
        <div aria-label="Bulk warming results" className="space-y-1 text-xs">
          {outcomes.map((outcome) => (
            <p key={outcome.itemId}>
              {outcome.itemId} — {outcome.status}
              {outcome.jobId ? ` · job ${outcome.jobId}` : ""}
              {outcome.reason
                ? ` · ${outcome.reason}`
                : ` · from ${outcome.start.toLocaleString()}, ${outcome.length === 0 ? "through end of file" : `${outcome.length.toLocaleString()} bytes`}`}
            </p>
          ))}
        </div>
      )}
      <p>
        {status?.available
          ? status.paused
            ? "Queue paused"
            : "Queue active"
          : "Activate Native cache and restart before warming media."}
      </p>
      <div className="flex flex-wrap gap-2">
        <Button
          type="button"
          disabled={previewBusy || !status?.available}
          onClick={() => void previewPolicies()}
        >
          Preview policies
        </Button>
        {previewBusy && (
          <Button type="button" onClick={() => previewRequest.current?.abort()}>
            Cancel policy preview
          </Button>
        )}
      </div>
      {predictions && (
        <div className="space-y-2">
          <p>
            Policy preview: {predictions.length} candidates (maximum 100). This does not enqueue
            warming.
          </p>
          {predictions.map((prediction, index) => (
            <div
              key={`${prediction.itemId}:${prediction.start}:${index}`}
              className="rounded border border-base-content/15 p-3"
            >
              <p>{prediction.displayName}</p>
              <p className="text-xs">
                {prediction.source} — {prediction.reason}
              </p>
              {prediction.eligible === false ? (
                <p className="text-xs">Not eligible for warming</p>
              ) : (
                <p className="text-xs">
                  {prediction.itemId} · {prediction.length.toLocaleString()} requested bytes from{" "}
                  {prediction.start.toLocaleString()} · file {prediction.fileSize.toLocaleString()}{" "}
                  bytes
                </p>
              )}
            </div>
          ))}
        </div>
      )}
      <fieldset
        disabled={busy || !status?.available || status?.healthy === false}
        className="space-y-3"
      >
        <div className="flex flex-wrap gap-2">
          <Button
            type="button"
            onClick={() => void operate(status?.paused ? "resume-all" : "pause-all")}
          >
            {status?.paused ? "Resume all warming" : "Pause all warming"}
          </Button>
          <Button type="button" onClick={() => void operate("sync")}>
            Sync Plex policies now
          </Button>
        </div>
        <label>
          Imported media IDs (up to 32)
          <Textarea
            aria-label="Imported media IDs (up to 32)"
            value={ids}
            onChange={(event) => setIds(event.target.value)}
            placeholder="Paste imported DAV item UUIDs, separated by commas or newlines."
          />
        </label>
        <div className="grid gap-3 md:grid-cols-2">
          <label>
            Range start (bytes)
            <Input
              aria-label="Range start (bytes)"
              type="number"
              min={0}
              value={start}
              onChange={(event) => setStart(Number(event.target.value))}
            />
          </label>
          <label>
            Range length (bytes, 0 means rest of file)
            <Input
              aria-label="Range length (bytes, 0 means rest of file)"
              type="number"
              min={0}
              value={length}
              onChange={(event) => setLength(Number(event.target.value))}
            />
          </label>
        </div>
        <p className="text-xs">
          {selected.length} selected.{" "}
          {length > 0
            ? `Requests up to ${length.toLocaleString()} bytes per item.`
            : "Requests the full remaining file; the server checks each file's size and per-item cap."}{" "}
          Daily byte and foreground-playback budgets still apply. Scheduled bytes are not yet
          cached.
        </p>
        <Button
          type="button"
          disabled={!valid}
          onClick={() => void operate("warm", { itemIds: selected, start, length })}
        >
          Warm selected media
        </Button>
      </fieldset>
      <label>
        Job state
        <select
          className="select"
          aria-label="Job state"
          value={filter}
          onChange={(event) => {
            setFilter(event.target.value);
            setPage(0);
          }}
        >
          <option value="all">All states</option>
          {[
            "queued",
            "running",
            "paused",
            "deferred",
            "failed",
            "completed",
            "cancelled",
            "expired",
          ].map((state) => (
            <option key={state}>{state}</option>
          ))}
        </select>
      </label>
      <div className="space-y-3">
        {jobs.slice(page * 25, (page + 1) * 25).map((job) => (
          <div key={job.id} className="space-y-2 rounded border border-base-content/15 p-3">
            <p>
              {job.displayName || job.itemId} — {job.state} ({job.source || job.trigger})
            </p>
            <p className="text-xs">
              {job.committedBytes.toLocaleString()}
              {job.fileSize ? ` of ${job.fileSize.toLocaleString()}` : ""} cached file bytes
              (whole-file coverage, not this range's progress).
            </p>
            {job.reason && <p className="text-xs">{job.reason}</p>}
            <p className="text-xs">
              Requested range: start {(job.start ?? 0).toLocaleString()},{" "}
              {job.length > 0 ? `${job.length.toLocaleString()} bytes` : "remaining file"}.
            </p>
            {job.error && <p>{job.error}</p>}
            <fieldset
              disabled={busy || !status?.available}
              className="flex flex-wrap items-center gap-2"
            >
              {["queued", "running", "deferred"].includes(job.state) && (
                <Button type="button" onClick={() => void operate("pause", { jobId: job.id })}>
                  Pause {job.id}
                </Button>
              )}
              {job.state === "paused" && (
                <Button type="button" onClick={() => void operate("resume", { jobId: job.id })}>
                  Resume {job.id}
                </Button>
              )}
              {["failed", "cancelled", "expired"].includes(job.state) && (
                <Button type="button" onClick={() => void operate("retry", { jobId: job.id })}>
                  Retry {job.id}
                </Button>
              )}
              {!["completed", "cancelled", "expired"].includes(job.state) && (
                <Button type="button" onClick={() => void operate("cancel", { jobId: job.id })}>
                  Cancel {job.id}
                </Button>
              )}
              <label>
                Priority for {job.id}
                <Input
                  aria-label={`Priority for ${job.id}`}
                  type="number"
                  min={-100}
                  max={100}
                  value={priorities[job.id] ?? job.priority}
                  onChange={(event) =>
                    setPriorities((current) => ({
                      ...current,
                      [job.id]: Number(event.target.value),
                    }))
                  }
                />
              </label>
              <Button
                type="button"
                disabled={
                  !Number.isInteger(priorities[job.id] ?? job.priority) ||
                  (priorities[job.id] ?? job.priority) < -100 ||
                  (priorities[job.id] ?? job.priority) > 100
                }
                onClick={() =>
                  void operate("prioritize", {
                    jobId: job.id,
                    priority: priorities[job.id] ?? job.priority,
                  })
                }
              >
                Set priority {job.id}
              </Button>
            </fieldset>
          </div>
        ))}
      </div>
      <div className="flex gap-2">
        <Button
          type="button"
          disabled={page === 0}
          onClick={() => setPage((current) => current - 1)}
        >
          Previous jobs
        </Button>
        <span>
          Page {page + 1} · {jobs.length} jobs
        </span>
        <Button
          type="button"
          disabled={(page + 1) * 25 >= jobs.length}
          onClick={() => setPage((current) => current + 1)}
        >
          Next jobs
        </Button>
      </div>
    </SettingsCard>
  );
}
