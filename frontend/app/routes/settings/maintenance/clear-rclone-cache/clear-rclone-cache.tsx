import { ConfirmModal } from "~/components/confirm-modal/confirm-modal";
import { useCallback, useState } from "react";
import { Button } from "~/components/ui/button";
import { Alert } from "~/components/ui/feedback";
import { Icon } from "~/components/ui/icon";
import { formatBytes } from "~/routes/settings/rclone/builtin-mount-model";
import { withUrlBase } from "~/utils/url-base";

export function ClearRcloneCache() {
  const [isRunning, setIsRunning] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [showConfirm, setShowConfirm] = useState(false);

  const onClear = useCallback(async () => {
    setShowConfirm(false);
    setIsRunning(true);
    setMessage(null);
    setError(null);
    try {
      const response = await fetch(withUrlBase("/api/rclone-mounts/clear-cache"), {
        method: "POST",
      });
      if (!response.ok) {
        // Error body from POST /api/rclone-mounts/clear-cache (BaseApiResponse).
        const body = (await response.json().catch(() => ({}))) as { error?: string };
        throw new Error(body.error || `Request failed (${response.status})`);
      }
      // Success body from POST /api/rclone-mounts/clear-cache (RcloneCacheClearedResponse).
      const data = (await response.json()) as {
        freedBytes?: number;
        mounted?: string[];
        errors?: string[];
      };
      if (data.errors?.length) {
        setError(data.errors.join(" "));
        return;
      }
      const remounted = data.mounted?.length ?? 0;
      setMessage(
        `Freed ${formatBytes(data.freedBytes ?? 0)}.` +
          (remounted > 0 ? ` Remounted ${remounted} share(s).` : ""),
      );
    } catch (e) {
      setError(e instanceof Error ? e.message : "Failed to clear the rclone cache.");
    } finally {
      setIsRunning(false);
    }
  }, []);

  return (
    <div className="space-y-4">
      <Alert className="alert-soft items-start py-3 text-sm" variant="warning">
        <Icon name="warning" className="!text-[20px]" />
        <div>
          <p className="font-semibold">Playback pauses while this runs</p>
          <p className="mt-0.5 text-xs opacity-80">
            The mounts are released, the cache directory is emptied, and the mounts are restored.
            Anything streaming through them will stall until they are back.
          </p>
        </div>
      </Alert>

      <p className="text-sm leading-relaxed text-base-content/70">
        Empty the built-in rclone VFS cache to reclaim disk space. Lowering the cache size limit
        does not delete what is already on disk; rclone evicts it only as it needs room.
      </p>

      <div className="rounded-lg border border-base-content/10 bg-base-200/40 p-3">
        <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
          <Button
            type="button"
            variant={isRunning ? "secondary" : "danger"}
            disabled={isRunning}
            className="shrink-0"
            onClick={() => setShowConfirm(true)}
          >
            <Icon
              name={isRunning ? "progress_activity" : "delete_sweep"}
              className={`!text-[18px] ${isRunning ? "animate-spin" : ""}`}
            />
            {isRunning ? "Clearing..." : "Clear Cache"}
          </Button>
          <div
            aria-live="polite"
            className={`min-w-0 break-words font-mono text-xs ${
              error ? "text-error" : message ? "text-success" : "text-base-content/70"
            }`}
          >
            {error ?? message ?? "Ready to clear."}
          </div>
        </div>
      </div>
      <ConfirmModal
        show={showConfirm}
        title="Clear the rclone cache?"
        message="The mounts are released, the cache directory is emptied, and the mounts are restored. Cached data is re-downloaded on demand, so playback is slower until the cache refills."
        confirmText="Clear cache"
        cancelText="Cancel"
        onCancel={() => setShowConfirm(false)}
        onConfirm={() => void onClear()}
      />
    </div>
  );
}
