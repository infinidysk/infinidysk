import type {
  OverviewWindow,
  ProviderCircuitState,
  ProviderRow,
} from "~/clients/backend-client.server";
import { formatBytes, formatNumber, formatPercent, formatSpeed } from "../../utils/format";
import { settingsPath } from "~/navigation/settings-tabs";
import { Icon, Tooltip } from "~/components/ui";
import { WidgetLink } from "../widget-link/widget-link";
import { ProviderSpeedChart } from "../provider-speed-chart/provider-speed-chart";

export type ProviderScoreboardProps = {
  providers: ProviderRow[];
  window: OverviewWindow;
  selectedProvider?: string | null;
  onSelectProvider?: (provider: string | null) => void;
  providerSpeedBucketSizeMs?: number;
  providerSpeedHistoryTruncated?: boolean;
};

export function ProviderScoreboard({
  providers,
  window,
  selectedProvider = null,
  onSelectProvider,
  providerSpeedBucketSizeMs = 900_000,
  providerSpeedHistoryTruncated = false,
}: ProviderScoreboardProps) {
  const total = providers.reduce((s, p) => s + p.articles, 0);
  const hasOpenCircuit = providers.some(
    (p) => p.circuitState === "open" || p.circuitState === "halfOpen",
  );
  const outageHelp = `Circuit-open time per ${outageIntervalLabel(window)} interval on a fixed 0–100% scale. Brief trips use a minimum-height tick.`;
  const speedHelp =
    "Historical average: bytes fetched divided by summed successful fetch durations over the selected window. Durations include connection-pool wait and overlap across concurrent fetches, so this is not wall-clock aggregate bandwidth. Use the provider benchmark for line-rate calibration.";
  const selected = providers.find((p) => p.provider === selectedProvider);

  return (
    <section className="card w-full min-w-0 border border-base-content/10 bg-base-100 shadow-sm">
      <div className="card-body gap-3 p-4">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div>
            <h3 className="card-title text-base">Providers</h3>
            <p className="text-xs text-base-content/50">
              Per-provider fetches, {window === "all" ? "all time" : `last ${window}`}
            </p>
          </div>
          <div className="card-actions m-0">
            <WidgetLink to={settingsPath("usenet")}>Usenet settings</WidgetLink>
          </div>
        </div>
        {hasOpenCircuit && (
          <p className="text-xs text-warning">
            A provider circuit is open or recovering.{" "}
            <WidgetLink to={settingsPath("usenet")}>Review Usenet settings</WidgetLink>
          </p>
        )}

        {providers.length === 0 ? (
          <p className="py-6 text-center text-xs text-base-content/50">No providers configured.</p>
        ) : (
          <>
            <div className="w-full min-w-0 overflow-x-auto lg:overflow-x-hidden">
              <table className="table table-pin-cols table-sm w-full">
                <thead>
                  <tr>
                    <th>Provider</th>
                    <th className="w-[120px] min-w-0 px-1">Activity</th>
                    <th className="w-[120px] min-w-0 px-1">
                      <Tooltip content={outageHelp}>
                        <span>Outages</span>
                      </Tooltip>
                    </th>
                    <th>Articles</th>
                    <th>Read</th>
                    <th>Share</th>
                    <th className="w-[120px] min-w-0 px-1">
                      <Tooltip content={speedHelp}>
                        <span>MB/s</span>
                      </Tooltip>
                    </th>
                    <th className="w-[120px] min-w-0 px-1">Errors</th>
                    <th className="w-[120px] min-w-0 px-1">Retries</th>
                    <th>
                      <Tooltip content="Mean duration of successful fetches only. Includes connection-pool wait inside the provider call — not pure wire RTT. Misses and errors are excluded.">
                        <span>Avg ok ms</span>
                      </Tooltip>
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {providers.map((p) => {
                    const share = total > 0 ? (p.articles / total) * 100 : 0;
                    const circuitState = p.circuitState ?? "closed";
                    return (
                      <tr key={p.provider}>
                        <th scope="row" className="bg-base-100 font-medium">
                          <div className="flex max-w-[260px] min-w-0 items-center gap-1">
                            <Tooltip content={buildProviderTooltip(p, circuitState)}>
                              <div className="flex min-w-0 items-center gap-2 font-medium">
                                <span
                                  className={`status status-xs shrink-0 ${statusClass(circuitState)}`}
                                />
                                <span className="min-w-0 truncate">
                                  {p.nickname?.trim() || p.provider}
                                </span>
                                {circuitState !== "closed" && (
                                  <span
                                    className={`badge badge-sm shrink-0 ${badgeClass(circuitState)}`}
                                  >
                                    {circuitLabel(circuitState, p.cooldownRemainingSeconds)}
                                  </span>
                                )}
                              </div>
                            </Tooltip>
                            {onSelectProvider && (
                              <button
                                type="button"
                                className="btn btn-ghost btn-xs shrink-0"
                                aria-expanded={selectedProvider === p.provider}
                                aria-controls="provider-speed-chart"
                                aria-label={`Show speed history for ${p.nickname?.trim() || p.provider}`}
                                onClick={() =>
                                  onSelectProvider(
                                    selectedProvider === p.provider ? null : p.provider,
                                  )
                                }
                              >
                                <Icon name="monitoring" className="!text-[16px]" />
                              </button>
                            )}
                          </div>
                        </th>
                        <td className="min-w-0 px-1">
                          <Sparkline values={p.spark} tone="success" />
                        </td>
                        <td className="min-w-0 px-1">
                          <Tooltip content={outageHelp}>
                            <OutageBuckets values={p.outageSpark ?? []} />
                          </Tooltip>
                        </td>
                        <td className="font-mono tabular-nums">{formatNumber(p.articles)}</td>
                        <td className="font-mono tabular-nums">{formatBytes(p.bytesFetched)}</td>
                        <td>
                          <ShareBar share={share} />
                        </td>
                        <td className="min-w-0 px-1">
                          <Tooltip content={speedHelp}>
                            <div className="flex flex-col gap-0.5">
                              <Sparkline values={p.speedSpark ?? []} tone="secondary" />
                              <div className="font-mono text-[11px] tabular-nums text-secondary">
                                {formatSpeed(p.speedMbPerSec)}
                              </div>
                            </div>
                          </Tooltip>
                        </td>
                        <td className="min-w-0 px-1">
                          <div className="flex flex-col gap-0.5">
                            <Sparkline values={p.errorSpark ?? []} tone="error" eventsOnly />
                            <div
                              className={`font-mono text-[11px] tabular-nums ${p.errorRate > 0.05 ? "text-error" : "text-base-content/60"}`}
                            >
                              {formatNumber(p.errors)}
                              {p.errorRate > 0 && (
                                <span className="text-base-content/50">
                                  {" "}
                                  ({formatPercent(p.errorRate * 100, 1)})
                                </span>
                              )}
                            </div>
                          </div>
                        </td>
                        <td className="min-w-0 px-1">
                          <div className="flex flex-col gap-0.5">
                            <Sparkline values={p.retrySpark ?? []} tone="warning" eventsOnly />
                            <div
                              className={`font-mono text-[11px] tabular-nums ${p.retries > 0 ? "text-warning" : "text-base-content/60"}`}
                            >
                              {formatNumber(p.retries)}
                            </div>
                          </div>
                        </td>
                        <td className="font-mono tabular-nums">
                          <Tooltip content="Successful fetches only (includes pool wait)">
                            <span>{p.avgDurationMs.toFixed(0)}</span>
                          </Tooltip>
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
            {selected && (
              <div id="provider-speed-chart">
                <ProviderSpeedChart
                  providerLabel={selected.nickname?.trim() || selected.provider}
                  points={selected.speedSeries ?? []}
                  bucketSizeMs={providerSpeedBucketSizeMs}
                  historyTruncated={providerSpeedHistoryTruncated}
                  window={window}
                />
              </div>
            )}
          </>
        )}
      </div>
    </section>
  );
}

function statusClass(state: ProviderCircuitState) {
  switch (state) {
    case "open":
      return "status-error";
    case "halfOpen":
      return "status-warning";
    default:
      return "status-success";
  }
}

function badgeClass(state: ProviderCircuitState) {
  switch (state) {
    case "open":
      return "badge-error";
    case "halfOpen":
      return "badge-warning";
    default:
      return "badge-success";
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
    lines.push(
      "Circuit half-open. Tried after the healthy providers of its tier until a request confirms it.",
    );
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
    <div className="flex items-center gap-2">
      <progress
        className="progress progress-success w-20"
        value={share}
        max={100}
        aria-label={`Article share: ${formatPercent(share, 0)}`}
      />
      <span className="font-mono text-[11px] tabular-nums">{formatPercent(share, 0)}</span>
    </div>
  );
}

function outageIntervalLabel(window: OverviewWindow) {
  switch (window) {
    case "1h":
      return "minute";
    case "all":
      return "day";
    default:
      return "hour";
  }
}

export function OutageBuckets({ values }: { values: number[] }) {
  if (values.length === 0)
    return <div className="h-[22px] w-[110px] rounded-sm bg-base-content/[0.04]" />;

  const w = 110;
  const h = 22;
  const baseline = h - 2;
  const chartHeight = h - 4;
  const slotWidth = w / values.length;
  const gap = Math.min(0.6, slotWidth * 0.18);
  const barWidth = Math.max(0.25, slotWidth - gap);
  const peak = Math.max(...values.map((value) => Math.min(100, Math.max(0, value))));

  return (
    <svg
      viewBox={`0 0 ${w} ${h}`}
      className="block h-[22px] w-[110px]"
      preserveAspectRatio="none"
      role="img"
      aria-label={`Circuit-open time by interval, peak ${peak}%`}
    >
      <line
        x1="0"
        y1={baseline}
        x2={w}
        y2={baseline}
        stroke="var(--color-base-content)"
        strokeOpacity="0.12"
        vectorEffect="non-scaling-stroke"
      />
      {values.map((value, index) => {
        const percent = Math.min(100, Math.max(0, value));
        if (percent === 0) return null;
        const barHeight = Math.max(1.5, (percent / 100) * chartHeight);
        return (
          <rect
            key={index}
            x={index * slotWidth + gap / 2}
            y={baseline - barHeight}
            width={barWidth}
            height={barHeight}
            rx={Math.min(0.5, barWidth / 2)}
            fill="var(--color-error)"
            fillOpacity="0.75"
          >
            <title>{`${percent}% circuit open during this interval`}</title>
          </rect>
        );
      })}
    </svg>
  );
}

type SparklineTone = "success" | "secondary" | "error" | "warning";

function buildEventPath(values: number[], step: number, y: (value: number) => number) {
  const parts: string[] = [];
  for (let index = 0; index < values.length;) {
    const v = values[index];
    if (v === undefined || v <= 0) {
      index++;
      continue;
    }

    const firstEventIndex = index;
    while (index + 1 < values.length) {
      const next = values[index + 1];
      if (next === undefined || next <= 0) break;
      index++;
    }
    const lastEventIndex = index;
    const startIndex = Math.max(0, firstEventIndex - 1);
    const endIndex = Math.min(values.length - 1, lastEventIndex + 1);
    for (let pointIndex = startIndex; pointIndex <= endIndex; pointIndex++) {
      const pv = values[pointIndex];
      if (pv === undefined) continue;
      parts.push(
        `${pointIndex === startIndex ? "M" : "L"}${(pointIndex * step).toFixed(1)},${y(pv).toFixed(1)}`,
      );
    }
    index = lastEventIndex + 1;
  }

  return parts.join(" ");
}

export function Sparkline({
  values,
  tone = "success",
  eventsOnly = false,
}: {
  values: number[];
  tone?: SparklineTone;
  eventsOnly?: boolean;
}) {
  if (values.length === 0)
    return <div className="h-[22px] w-[110px] rounded-sm bg-base-content/[0.04]" />;
  const w = 110;
  const h = 22;
  const max = Math.max(1, ...values);
  const step = values.length > 1 ? w / (values.length - 1) : 0;
  const y = (v: number) => h - (v / max) * (h - 4) - 2;
  const path = values
    .map((v, i) => `${i === 0 ? "M" : "L"}${(i * step).toFixed(1)},${y(v).toFixed(1)}`)
    .join(" ");
  const area = `${path} L${((values.length - 1) * step).toFixed(1)},${h} L0,${h} Z`;
  const colorVar = sparklineColor(tone);
  const fill = `color-mix(in srgb, ${colorVar} 16%, transparent)`;
  const eventPath = eventsOnly ? buildEventPath(values, step, y) : path;
  return (
    <svg viewBox={`0 0 ${w} ${h}`} className="block h-[22px] w-[110px]" preserveAspectRatio="none">
      {eventsOnly ? (
        <path
          d={path}
          fill="none"
          stroke="var(--color-base-content)"
          strokeOpacity="0.12"
          strokeWidth="1.2"
          vectorEffect="non-scaling-stroke"
        />
      ) : (
        <path d={area} fill={fill} />
      )}
      {eventPath && (
        <path
          d={eventPath}
          fill="none"
          stroke={colorVar}
          strokeWidth="1.2"
          vectorEffect="non-scaling-stroke"
        />
      )}
    </svg>
  );
}

function sparklineColor(tone: SparklineTone) {
  if (tone === "error") return "var(--color-error)";
  if (tone === "warning") return "var(--color-warning)";
  if (tone === "secondary") return "var(--color-secondary)";
  return "var(--color-success)";
}
