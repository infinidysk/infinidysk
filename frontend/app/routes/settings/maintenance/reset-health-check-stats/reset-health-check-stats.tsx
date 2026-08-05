import { useCallback, useState } from "react";
import { Button } from "~/components/ui/button";
import { Alert } from "~/components/ui/feedback";
import { Icon } from "~/components/ui/icon";
import { withUrlBase } from "~/utils/url-base";

export function ResetHealthCheckStats() {
    const [isRunning, setIsRunning] = useState(false);
    const [message, setMessage] = useState<string | null>(null);
    const [error, setError] = useState<string | null>(null);

    const onReset = useCallback(async () => {
        if (!window.confirm(
            "Reset all health-check statistics and history? This cannot be undone."
        )) {
            return;
        }

        setIsRunning(true);
        setMessage(null);
        setError(null);
        try {
            const response = await fetch(withUrlBase("/api/clear-health-check-history"), { method: "POST" });
            if (!response.ok) {
                const body = await response.json().catch(() => ({}));
                throw new Error(body.error || `Request failed (${response.status})`);
            }
            const data = await response.json();
            setMessage(
                `Reset complete. Removed ${data.deletedResults ?? 0} result row(s) and ${data.deletedStats ?? 0} stat row(s).`
            );
        } catch (e) {
            setError(e instanceof Error ? e.message : "Failed to reset health-check statistics.");
        } finally {
            setIsRunning(false);
        }
    }, []);

    return (
        <div className="space-y-4">
            <Alert className="alert-soft items-start py-3 text-sm" variant="warning">
                <Icon name="warning" className="!text-[20px]" />
                <div>
                    <p className="font-semibold">This cannot be undone</p>
                    <p className="mt-0.5 text-xs opacity-80">
                        All accumulated health-check results and counters will be permanently removed.
                    </p>
                </div>
            </Alert>

            <p className="text-sm leading-relaxed text-base-content/70">
                Clear health-check history and reset repair, deletion, healthy, and unhealthy counters.
            </p>

            <div className="rounded-lg border border-base-content/10 bg-base-200/40 p-3">
                <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
                    <Button
                        type="button"
                        variant={isRunning ? "secondary" : "danger"}
                        disabled={isRunning}
                        className="shrink-0"
                        onClick={onReset}
                    >
                        <Icon
                            name={isRunning ? "progress_activity" : "delete_sweep"}
                            className={`!text-[18px] ${isRunning ? "animate-spin" : ""}`}
                        />
                        {isRunning ? "Resetting..." : "Reset Statistics"}
                    </Button>
                    <div
                        aria-live="polite"
                        className={`min-w-0 break-words font-mono text-xs ${
                            error
                                ? "text-error"
                                : message
                                    ? "text-success"
                                    : "text-base-content/70"
                        }`}
                    >
                        {error ?? message ?? "Ready to reset."}
                    </div>
                </div>
            </div>
        </div>
    );
}
