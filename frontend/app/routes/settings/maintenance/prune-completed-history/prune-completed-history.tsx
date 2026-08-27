import { Button } from "~/components/ui/button";
import { Alert } from "~/components/ui/feedback";
import { Input, Select } from "~/components/ui/form";
import { Icon } from "~/components/ui/icon";
import { useCallback, useEffect, useMemo, useState } from "react";
import { useWebsocketTopic } from "~/utils/shared-websocket";
import { withUrlBase } from "~/utils/url-base";

type PruneCompletedHistoryProps = { savedConfig: Record<string, string> };

export function PruneCompletedHistory({ savedConfig }: PruneCompletedHistoryProps) {
  const [connected, setConnected] = useState(false);
  const [progress, setProgress] = useState<string | null>(null);
  const [isFetching, setIsFetching] = useState(false);
  const [runStarted, setRunStarted] = useState(false);
  const [statusError, setStatusError] = useState<string | null>(null);
  const [category, setCategory] = useState("");
  const [olderThanDays, setOlderThanDays] = useState("");
  const categories = useMemo(
    () =>
      (savedConfig["api.categories"] ?? "")
        .split(",")
        .map((c) => c.trim())
        .filter(Boolean),
    [savedConfig],
  );
  const progressMessage = progress?.replace("Dry Run - ", "");
  const isFinished =
    progressMessage?.startsWith("Done") ||
    progressMessage?.startsWith("Failed") ||
    progressMessage?.startsWith("Aborted");
  const isRunning = !isFinished && (isFetching || runStarted);
  useWebsocketTopic("pchp", "state", setProgress, {
    onOpen: () => setConnected(true),
    onClose: () => setProgress(null),
  });
  useEffect(() => {
    if (isFinished) setRunStarted(false);
  }, [isFinished]);
  const buildQueryString = useCallback(() => {
    const params = new URLSearchParams();
    if (category) params.set("category", category);
    const days = parseInt(olderThanDays, 10);
    if (!Number.isNaN(days) && days > 0) params.set("older-than-days", String(days));
    const qs = params.toString();
    return qs ? `?${qs}` : "";
  }, [category, olderThanDays]);
  const startTask = useCallback(
    async (url: string) => {
      setStatusError(null);
      setProgress(null);
      setRunStarted(true);
      setIsFetching(true);
      try {
        const response = await fetch(withUrlBase(`${url}${buildQueryString()}`));
        if (response.status === 409) {
          setStatusError("Task already running.");
          setRunStarted(false);
          return;
        }
        if (!response.ok) {
          setStatusError(`Request failed (${response.status}).`);
          setRunStarted(false);
        }
      } catch {
        setStatusError("Request failed.");
        setRunStarted(false);
      } finally {
        setIsFetching(false);
      }
    },
    [buildQueryString],
  );
  return (
    <>
      <Alert className="alert-soft mb-4 items-start py-3 text-sm" variant="warning">
        <Icon name="backup" className="!text-[20px]" />
        <div>
          <p className="font-semibold">Back up before cleanup</p>
          <p className="mt-0.5 text-xs opacity-80">
            Pruning SAB history is permanent. WebDAV files are preserved, but they lose history
            protection — files not linked from the organized library will show up in Remove Orphaned
            Files (including a scheduled run).
          </p>
        </div>
      </Alert>
      <div className="space-y-4">
        <p className="text-sm text-base-content/70">
          Remove completed SAB history rows in bulk without deleting mounted WebDAV content.
        </p>
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
          <label className="space-y-1.5">
            <span className="block text-xs font-medium text-base-content/70">Category</span>
            <Select
              className="w-full"
              value={category}
              onChange={(e) => setCategory(e.target.value)}
              disabled={isRunning}
            >
              <option value="">All categories</option>
              {categories.map((cat) => (
                <option key={cat} value={cat}>
                  {cat}
                </option>
              ))}
            </Select>
          </label>
          <label className="space-y-1.5">
            <span className="block text-xs font-medium text-base-content/70">
              Older than (days)
            </span>
            <Input
              className="w-full max-w-48"
              type="number"
              min={0}
              placeholder="Any age"
              value={olderThanDays}
              onChange={(e) => setOlderThanDays(e.target.value)}
              disabled={isRunning}
            />
          </label>
        </div>
        <div className="rounded-lg border border-base-content/10 bg-base-200/40 p-3 flex flex-wrap gap-2 items-center justify-between">
          <div className="flex gap-2">
            <Button
              variant={connected && !isRunning ? "danger" : "secondary"}
              disabled={!connected || isRunning}
              onClick={() => void startTask("/api/prune-completed-history")}
            >
              {isRunning ? "Running..." : "Run Task"}
            </Button>
            <Button
              variant="outline"
              size="small"
              disabled={!connected || isRunning}
              onClick={() => void startTask("/api/prune-completed-history/dry-run")}
            >
              Dry Run
            </Button>
          </div>
          <div
            aria-live="polite"
            className="font-mono text-xs text-base-content/70 whitespace-pre-line"
          >
            {statusError ?? progress ?? "Ready to prune."}
          </div>
        </div>
      </div>
    </>
  );
}
