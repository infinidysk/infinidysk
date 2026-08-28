import { useCallback, useState } from "react";
import { Button } from "~/components/ui/button";
import { Alert } from "~/components/ui/feedback";
import { Icon } from "~/components/ui/icon";
import { withUrlBase } from "~/utils/url-base";

export function ResetHealthCheckQueue() {
  const [isRunning, setIsRunning] = useState(false);
  const [queuedCount, setQueuedCount] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);

  const onQueue = useCallback(async () => {
    if (
      !window.confirm(
        "Queue a fresh health-check pass over every video, audio, and archive file in the library? " +
          "This generates significant Usenet (STAT) traffic on large libraries.",
      )
    ) {
      return;
    }

    setIsRunning(true);
    setQueuedCount(null);
    setError(null);
    try {
      const response = await fetch(withUrlBase("/api/reset-health-check-queue"), {
        method: "POST",
      });
      if (!response.ok) {
        // Error body from POST /api/reset-health-check-queue (BaseApiResponse).
        const body = (await response.json().catch(() => ({}))) as { error?: string };
        throw new Error(body.error || `Request failed (${response.status})`);
      }
      // Success body from POST /api/reset-health-check-queue (ResetHealthCheckQueueResponse).
      const data = (await response.json()) as { resetCount?: number };
      setQueuedCount(data.resetCount ?? 0);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to queue library health checks.");
    } finally {
      setIsRunning(false);
    }
  }, []);

  return (
    <div className="space-y-4">
      <Alert className="alert-soft items-start py-3 text-sm" variant="info">
        <Icon name="info" className="!text-[20px]" />
        <div>
          <p className="font-semibold">Covers your whole media library</p>
          <p className="mt-0.5 text-xs opacity-80">
            Includes files still present in SAB history — no history is deleted. Existing
            health-check results and statistics are kept.
          </p>
        </div>
      </Alert>

      <p className="text-sm leading-relaxed text-base-content/70">
        Queue a fresh background health-check pass over every video, audio, and archive file in the
        library. Checks run a few files at a time, pause while downloads are processing, and can
        generate significant Usenet (STAT) traffic on large libraries.
      </p>

      <div className="rounded-lg border border-base-content/10 bg-base-200/40 p-3">
        <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
          <Button
            type="button"
            variant={isRunning ? "secondary" : "primary"}
            disabled={isRunning}
            className="shrink-0"
            onClick={() => void onQueue()}
          >
            <Icon
              name={isRunning ? "progress_activity" : "health_and_safety"}
              className={`!text-[18px] ${isRunning ? "animate-spin" : ""}`}
            />
            {isRunning ? "Queueing..." : "Re-run Health Checks"}
          </Button>
          <div
            aria-live="polite"
            className={`min-w-0 break-words font-mono text-xs ${
              error ? "text-error" : queuedCount !== null ? "text-success" : "text-base-content/70"
            }`}
          >
            {error ??
              (queuedCount !== null ? (
                <>
                  {`Queued ${queuedCount.toLocaleString()} file(s) for re-check. Track progress on the `}
                  <a className="link font-medium" href={withUrlBase("/health")}>
                    Health page
                  </a>
                  .
                </>
              ) : (
                "Ready to queue."
              ))}
          </div>
        </div>
      </div>
    </div>
  );
}
