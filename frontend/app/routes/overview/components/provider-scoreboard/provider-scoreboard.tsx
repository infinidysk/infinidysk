import type { OverviewWindow, ProviderCircuitState, ProviderRow } from "~/clients/backend-client.server";
import { formatBytes, formatNumber, formatPercent } from "../../utils/format";

export type ProviderScoreboardProps = {
    providers: ProviderRow[],
    window: OverviewWindow,
}

export function ProviderScoreboard({ providers, window }: ProviderScoreboardProps) {
    const total = providers.reduce((s, p) => s + p.articles, 0);

    return (
        <section className="card w-full min-w-0 border border-base-content/10 bg-base-100 shadow-sm">
            <div className="card-body gap-3 p-4">
                <div>
                    <h3 className="card-title text-base">Providers</h3>
                    <p className="text-xs text-base-content/50">
                        Per-provider fetches, {window === "all" ? "all time" : `last ${window}`}
                    </p>
                </div>

                {providers.length === 0 ? (
                    <p className="py-6 text-center text-xs text-base-content/50">No providers configured.</p>
                ) : (
                    <div className="overflow-x-auto">
                        <table className="table table-sm min-w-[680px]">
                            <thead>
                                <tr>
                                    <th>Provider</th>
                                    <th className="w-[120px]">Activity</th>
                                    <th
                                        className="w-[120px]"
                                        title="Relative circuit-open history for each interval (scaled to the window’s peak)">
                                        Outages
                                    </th>
                                    <th>Articles</th>
                                    <th>Read</th>
                                    <th>Share</th>
                                    <th className="w-[120px]">Errors</th>
                                    <th className="w-[120px]">Retries</th>
                                    <th
                                        title="Mean duration of successful fetches only. Includes connection-pool wait inside the provider call — not pure wire RTT. Misses and errors are excluded.">
                                        Avg ok ms
                                    </th>
                                </tr>
                            </thead>
                            <tbody>
                                {providers.map(p => {
                                    const share = total > 0 ? (p.articles / total) * 100 : 0;
                                    const circuitState = p.circuitState ?? "closed";
                                    return (
                                        <tr key={p.provider}>
                                            <td>
                                                <div
                                                    className="flex max-w-[240px] min-w-0 items-center gap-2 font-medium"
                                                    title={buildProviderTooltip(p, circuitState)}>
                                                    <span className={`h-1.5 w-1.5 shrink-0 rounded-full ${dotClass(circuitState)}`} />
                                                    <span className="min-w-0 truncate">
                                                        {p.nickname?.trim() || p.provider}
                                                    </span>
                                                    {circuitState !== "closed" && (
                                                        <span className={`badge badge-sm shrink-0 ${badgeClass(circuitState)}`}>
                                                            {circuitLabel(circuitState, p.cooldownRemainingSeconds)}
                                                        </span>
                                                    )}
                                                </div>
                                            </td>
                                            <td>
                                                <Sparkline values={p.spark} tone="success" />
                                            </td>
                                            <td title="Relative provider circuit-open history">
                                                <Sparkline
                                                    values={p.outageSpark ?? []}
                                                    tone="error" />
                                            </td>
                                            <td className="font-mono tabular-nums">{formatNumber(p.articles)}</td>
                                            <td className="font-mono tabular-nums">{formatBytes(p.bytesFetched)}</td>
                                            <td>
                                                <ShareBar share={share} />
                                            </td>
                                            <td>
                                                <div className="flex flex-col gap-0.5">
                                                    <Sparkline values={p.errorSpark ?? []} tone="error" />
                                                    <div className={`font-mono text-[11px] tabular-nums ${p.errorRate > 0.05 ? "text-error" : "text-base-content/60"}`}>
                                                        {formatNumber(p.errors)}
                                                        {p.errorRate > 0 && (
                                                            <span className="text-base-content/50">
                                                                {" "}({formatPercent(p.errorRate * 100, 1)})
                                                            </span>
                                                        )}
                                                    </div>
                                                </div>
                                            </td>
                                            <td>
                                                <div className="flex flex-col gap-0.5">
                                                    <Sparkline values={p.retrySpark ?? []} tone="warning" />
                                                    <div className={`font-mono text-[11px] tabular-nums ${p.retries > 0 ? "text-warning" : "text-base-content/60"}`}>
                                                        {formatNumber(p.retries)}
                                                    </div>
                                                </div>
                                            </td>
                                            <td
                                                className="font-mono tabular-nums"
                                                title="Successful fetches only (includes pool wait)">
                                                {p.avgDurationMs.toFixed(0)}
                                            </td>
                                        </tr>
                                    );
                                })}
                            </tbody>
                        </table>
                    </div>
                )}
            </div>
        </section>
    );
}

function dotClass(state: ProviderCircuitState) {
    switch (state) {
        case "open": return "bg-error";
        case "halfOpen": return "bg-warning";
        default: return "bg-success";
    }
}

function badgeClass(state: ProviderCircuitState) {
    switch (state) {
        case "open": return "badge-error";
        case "halfOpen": return "badge-warning";
        default: return "badge-success";
    }
}

function circuitLabel(state: ProviderCircuitState, cooldownRemainingSeconds?: number | null) {
    if (state === "open") {
        return cooldownRemainingSeconds != null && cooldownRemainingSeconds > 0
            ? `Tripped · ${cooldownRemainingSeconds}s`
            : "Tripped";
    }
    if (state === "halfOpen") return "Recovering";
    return "Healthy";
}

function buildProviderTooltip(p: ProviderRow, state: ProviderCircuitState) {
    const lines = [p.nickname?.trim() || p.provider];
    if (state === "open") {
        lines.push("Circuit open — provider temporarily skipped after repeated failures.");
        if (p.cooldownRemainingSeconds != null && p.cooldownRemainingSeconds > 0)
            lines.push(`Retry in about ${p.cooldownRemainingSeconds}s.`);
    } else if (state === "halfOpen") {
        lines.push("Circuit half-open. Tried after the healthy providers of its tier until a request confirms it.");
    } else {
        lines.push("Circuit closed — provider is healthy.");
    }
    if (p.lastFailureReason) lines.push(`Last trip: ${p.lastFailureReason}`);
    if ((p.tripCount ?? 0) > 0) lines.push(`Trips (lifetime): ${p.tripCount}`);
    if ((p.failureCount ?? 0) > 0) lines.push(`Recorded failures: ${p.failureCount}`);
    if ((p.articleMissCount ?? 0) > 0) lines.push(`Article misses: ${p.articleMissCount}`);
    return lines.join("\n");
}

function ShareBar({ share }: { share: number }) {
    return (
        <div className="relative h-4 w-20 overflow-hidden rounded bg-base-200">
            <div className="absolute inset-0 bg-success opacity-25" style={{ width: `${share.toFixed(1)}%` }} />
            <span className="relative block px-1.5 text-center text-[11px] leading-4 text-base-content tabular-nums">
                {formatPercent(share, 0)}
            </span>
        </div>
    );
}

function Sparkline({
    values,
    tone = "success",
    fixedMax,
}: {
    values: number[],
    tone?: "success" | "error" | "warning",
    fixedMax?: number,
}) {
    if (values.length === 0) return <div className="h-[22px] w-[110px] rounded-sm bg-base-content/[0.04]" />;
    const w = 110;
    const h = 22;
    const max = fixedMax ?? Math.max(1, ...values);
    const step = values.length > 1 ? w / (values.length - 1) : 0;
    const y = (v: number) => h - (v / max) * (h - 4) - 2;
    const path = values
        .map((v, i) => `${i === 0 ? "M" : "L"}${(i * step).toFixed(1)},${y(v).toFixed(1)}`)
        .join(" ");
    const area = `${path} L${((values.length - 1) * step).toFixed(1)},${h} L0,${h} Z`;
    const colorVar = tone === "error"
        ? "var(--color-error)"
        : tone === "warning"
            ? "var(--color-warning)"
            : "var(--color-success)";
    const fill = `color-mix(in srgb, ${colorVar} 16%, transparent)`;
    return (
        <svg viewBox={`0 0 ${w} ${h}`} className="block h-[22px] w-[110px]" preserveAspectRatio="none">
            <path d={area} fill={fill} />
            <path d={path} fill="none" stroke={colorVar} strokeWidth="1.2" vectorEffect="non-scaling-stroke" />
        </svg>
    );
}
