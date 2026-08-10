import { useCallback, useEffect, useState } from "react";
import { Button } from "~/components/ui/button";
import { Alert } from "~/components/ui/feedback";
import { Select } from "~/components/ui/form";
import { Icon } from "~/components/ui/icon";
import { withUrlBase } from "~/utils/url-base";

type ProviderOption = { providerId: string; label: string };

// Response shape of GET /api/get-provider-usage (GetProviderUsageResponse).
type ProviderUsageItem = {
    providerId?: string | null;
    host: string;
    nickname?: string | null;
};

type ProviderUsageResponse = {
    providers?: ProviderUsageItem[];
};

export function ResetOverviewStats() {
    const [isRunning, setIsRunning] = useState(false);
    const [message, setMessage] = useState<string | null>(null);
    const [error, setError] = useState<string | null>(null);
    const [providers, setProviders] = useState<ProviderOption[]>([]);
    const [target, setTarget] = useState<string>(""); // "" = all providers

    useEffect(() => {
        let cancelled = false;
        fetch(withUrlBase("/api/get-provider-usage"))
            .then(r => (r.ok ? r.json() as Promise<ProviderUsageResponse> : Promise.reject(new Error("Failed to load provider usage"))))
            .then(data => {
                if (cancelled) return;
                setProviders(
                    (data.providers ?? [])
                        .filter((p): p is ProviderUsageItem & { providerId: string } => Boolean(p.providerId))
                        .map((p) => ({
                            providerId: p.providerId,
                            label: p.nickname?.trim() || p.host,
                        }))
                );
            })
            .catch(() => {
                /* selector falls back to "All providers" only */
            });
        return () => {
            cancelled = true;
        };
    }, []);

    const onReset = useCallback(async () => {
        const targetLabel = providers.find(p => p.providerId === target)?.label;
        const prompt = target
            ? `Reset Overview statistics for "${targetLabel}"? Its scoreboard, ` +
              "latency, and failover data will be permanently removed. Global " +
              "throughput charts and session history are unaffected. " +
              "This cannot be undone."
            : "Reset all Overview statistics? Throughput history, provider " +
              "scoreboard, sessions, failover and latency data will be " +
              "permanently removed. Queue, history, and provider data-cap " +
              "gauges are not affected. This cannot be undone.";
        if (!window.confirm(prompt)) return;

        setIsRunning(true);
        setMessage(null);
        setError(null);
        try {
            const url = target
                ? `/api/clear-overview-stats?provider=${encodeURIComponent(target)}`
                : "/api/clear-overview-stats";
            const response = await fetch(url, { method: "POST" });
            if (!response.ok) {
                // Error body from POST /api/clear-overview-stats (BaseApiResponse).
                const body = await response.json().catch(() => ({})) as { error?: string };
                throw new Error(body.error || `Request failed (${response.status})`);
            }
            // Success body from POST /api/clear-overview-stats (ClearOverviewStatsResponse).
            const data = await response.json() as { deletedRows?: number };
            setMessage(`Reset complete. Removed ${data.deletedRows ?? 0} metric row(s).`);
        } catch (e) {
            setError(e instanceof Error ? e.message : "Failed to reset Overview statistics.");
        } finally {
            setIsRunning(false);
        }
    }, [target, providers]);

    return (
        <div className="space-y-4">
            <Alert className="alert-soft items-start py-3 text-sm" variant="warning">
                <Icon name="warning" className="!text-[20px]" />
                <div>
                    <p className="font-semibold">This cannot be undone</p>
                    <p className="mt-0.5 text-xs opacity-80">
                        Overview metrics are permanently removed. Queue, history, and
                        provider data-cap gauges are preserved. A per-provider reset
                        leaves global throughput charts and session history intact.
                    </p>
                </div>
            </Alert>

            <p className="text-sm leading-relaxed text-base-content/70">
                Clear Overview statistics for all providers or a single provider.
                May take a few moments on large databases.
            </p>

            <div className="space-y-2">
                <label className="block text-sm font-medium text-base-content" htmlFor="reset-overview-stats-provider">
                    Scope
                </label>
                <Select
                    id="reset-overview-stats-provider"
                    className="w-full max-w-md"
                    value={target}
                    disabled={isRunning}
                    onChange={e => setTarget(e.target.value)}
                >
                    <option value="">All providers (full reset)</option>
                    {providers.map(p => (
                        <option key={p.providerId} value={p.providerId}>
                            {p.label}
                        </option>
                    ))}
                </Select>
            </div>

            <div className="rounded-lg border border-base-content/10 bg-base-200/40 p-3">
                <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
                    <Button
                        type="button"
                        variant={isRunning ? "secondary" : "danger"}
                        disabled={isRunning}
                        className="shrink-0"
                        onClick={() => void onReset()}
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
