import { useMemo, useState } from "react";
import styles from "./activity-heatmap.module.css";
import type { HeatmapCell, HeatmapMode } from "~/clients/backend-client.server";
import { formatNumber } from "../../utils/format";
import { Tooltip } from "~/components/ui";

export type ActivityHeatmapProps = {
  maxCell: number;
  mode: HeatmapMode;
  windowStartMs: number;
  windowEndMs: number;
  bucketSizeMs: number;
  cells: HeatmapCell[];
};

const ONE_HOUR = 3_600_000;
const ONE_DAY = 86_400_000;
const ONE_WEEK = 7 * ONE_DAY;
const DOW_LABELS = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
const MONTH_LABELS = [
  "Jan",
  "Feb",
  "Mar",
  "Apr",
  "May",
  "Jun",
  "Jul",
  "Aug",
  "Sep",
  "Oct",
  "Nov",
  "Dec",
];

type GridCell = {
  bucket: number;
  count: number;
  inRange: boolean;
};

type GridRow = {
  label: string;
  title?: string;
  cells: GridCell[];
};

type GridShape = {
  rows: GridRow[];
  cols: number;
  columnLabels?: { index: number; label: string }[];
};

export function ActivityHeatmap({
  maxCell,
  mode,
  windowStartMs,
  windowEndMs,
  bucketSizeMs,
  cells,
}: ActivityHeatmapProps) {
  const [hover, setHover] = useState<GridCell | null>(null);

  const grid = useMemo(
    () => buildGrid(mode, windowStartMs, windowEndMs, cells),
    [mode, windowStartMs, windowEndMs, cells],
  );

  const total = useMemo(() => cells.reduce((s, c) => s + c.count, 0), [cells]);
  const empty = total === 0;

  const peak = useMemo(() => {
    let best: HeatmapCell | null = null;
    for (const c of cells) if (!best || c.count > best.count) best = c;
    return best;
  }, [cells]);

  const subtitle = subtitleFor(mode);
  const emptyMessage = emptyMessageFor(mode);
  const peakLabel = peak ? formatBucket(peak.bucket, bucketSizeMs) : null;

  return (
    <section className="card w-full min-w-0 overflow-hidden border border-base-content/10 bg-base-100 shadow-sm">
      <div className="card-body gap-3 p-4">
        <div className="flex flex-wrap items-start justify-between gap-4">
          <div>
            <h3 className="card-title text-base">Activity heatmap</h3>
            <p className="text-xs text-base-content/50">{subtitle}</p>
          </div>
          {peak && peak.count > 0 && peakLabel && (
            <div className="flex flex-col items-end gap-px text-right">
              <span className="text-[10px] font-medium tracking-wide text-base-content/50 uppercase">
                Peak
              </span>
              <span className="text-sm font-semibold tracking-tight text-base-content tabular-nums">
                {peakLabel}
              </span>
              <span className="text-[11px] text-base-content/50 tabular-nums">
                {formatNumber(peak.count)} articles
              </span>
            </div>
          )}
        </div>

        {empty ? (
          <div className="py-10 text-center text-xs text-base-content/50">{emptyMessage}</div>
        ) : (
          <>
            <div className={styles.grid} data-mode={mode}>
              {grid.rows.map((row, r) => (
                <div key={r} className={styles.row}>
                  <Tooltip content={row.title ?? row.label}>
                    <div className="w-[30px] shrink-0 text-right text-[11px] font-medium text-base-content/50 select-none">
                      {row.label}
                    </div>
                  </Tooltip>
                  <div
                    className={styles.cellRow}
                    style={{ gridTemplateColumns: `repeat(${grid.cols}, minmax(0, 1fr))` }}
                  >
                    {row.cells.map((cell, c) => {
                      if (!cell.inRange) {
                        return <div key={c} className={styles.cellEmpty} />;
                      }
                      const intensity = maxCell > 0 ? cell.count / maxCell : 0;
                      return (
                        <button
                          type="button"
                          key={c}
                          aria-label={`${formatBucket(cell.bucket, bucketSizeMs)}: ${formatNumber(cell.count)} ${cell.count === 1 ? "article" : "articles"}`}
                          data-tip={`${formatBucket(cell.bucket, bucketSizeMs)}: ${formatNumber(cell.count)} ${cell.count === 1 ? "article" : "articles"}`}
                          className={`tooltip ${styles.cell}`}
                          style={{ backgroundColor: cellColor(intensity) }}
                          onMouseEnter={() => setHover(cell)}
                          onMouseLeave={() =>
                            setHover((h) => (h && h.bucket === cell.bucket ? null : h))
                          }
                          onFocus={() => setHover(cell)}
                          onBlur={() => setHover((h) => (h && h.bucket === cell.bucket ? null : h))}
                        />
                      );
                    })}
                  </div>
                </div>
              ))}
              {grid.columnLabels && grid.columnLabels.length > 0 && (
                <div className="mt-1 flex w-full min-w-0 items-center gap-2">
                  <div className="w-[30px] shrink-0" aria-hidden />
                  <div
                    className={`${styles.axisGrid} text-[10px] text-base-content/50 tabular-nums select-none`}
                    style={{ gridTemplateColumns: `repeat(${grid.cols}, minmax(0, 1fr))` }}
                  >
                    {grid.columnLabels.map((c, i) => (
                      <span
                        key={i}
                        className={styles.axisTick}
                        style={{ gridColumnStart: c.index + 1 }}
                      >
                        {c.label}
                      </span>
                    ))}
                  </div>
                </div>
              )}
            </div>

            <div className="mt-3 flex flex-wrap items-center justify-between gap-4">
              <div className="text-[11px] text-base-content/50 tabular-nums">
                {hover ? (
                  <>
                    {formatBucket(hover.bucket, bucketSizeMs)} &mdash; {formatNumber(hover.count)}{" "}
                    {hover.count === 1 ? "article" : "articles"}
                  </>
                ) : (
                  <>Select a cell for details</>
                )}
              </div>
              <div className="flex items-center gap-1 text-[10px] text-base-content/50">
                <span>Less</span>
                <div className="h-3 w-3 rounded-sm" style={{ backgroundColor: cellColor(0) }} />
                <div className="h-3 w-3 rounded-sm" style={{ backgroundColor: cellColor(0.25) }} />
                <div className="h-3 w-3 rounded-sm" style={{ backgroundColor: cellColor(0.5) }} />
                <div className="h-3 w-3 rounded-sm" style={{ backgroundColor: cellColor(0.75) }} />
                <div className="h-3 w-3 rounded-sm" style={{ backgroundColor: cellColor(1) }} />
                <span>More</span>
              </div>
            </div>
          </>
        )}
      </div>
    </section>
  );
}

function buildGrid(
  mode: HeatmapMode,
  windowStartMs: number,
  windowEndMs: number,
  cells: HeatmapCell[],
): GridShape {
  if (mode === "day") {
    const byBucket = new Map<number, number>();
    for (const c of cells) byBucket.set(c.bucket, c.count);
    const row: GridCell[] = [];
    for (let h = 0; h < 24; h++) {
      const bucket = windowStartMs + h * ONE_HOUR;
      row.push({ bucket, count: byBucket.get(bucket) ?? 0, inRange: bucket <= windowEndMs });
    }
    return {
      rows: [{ label: "Hours", cells: row }],
      cols: 24,
      columnLabels: rollingHourLabels(row),
    };
  }

  if (mode === "week" || mode === "month") {
    const counts = new Map<string, number>();
    for (const c of cells) {
      const key = `${startOfLocalDay(c.bucket)}:${new Date(c.bucket).getHours()}`;
      counts.set(key, (counts.get(key) ?? 0) + c.count);
    }

    const firstDay = startOfLocalDay(windowStartMs);
    const lastDay = startOfLocalDay(windowEndMs);
    const dayCount = Math.round((lastDay - firstDay) / ONE_DAY) + 1;

    const rows: GridRow[] = [];
    for (let d = 0; d < dayCount; d++) {
      const dayStart = addLocalDays(firstDay, d);
      const cellsRow: GridCell[] = [];
      for (let h = 0; h < 24; h++) {
        const instant = dayStart + h * ONE_HOUR;
        cellsRow.push({
          bucket: instant,
          count: counts.get(`${dayStart}:${h}`) ?? 0,
          inRange: instant >= windowStartMs && instant <= windowEndMs,
        });
      }
      rows.push({
        label: rowDateLabel(dayStart, mode),
        title: formatBucket(dayStart, ONE_DAY),
        cells: cellsRow,
      });
    }
    return { rows, cols: 24, columnLabels: hourAxisLabels() };
  }

  const firstMonday = startOfLocalWeek(windowStartMs);
  const counts = new Map<string, number>();
  for (const c of cells) {
    const day = startOfLocalDay(c.bucket);
    const week = Math.round((startOfLocalWeek(day) - firstMonday) / ONE_WEEK);
    counts.set(`${localDow(day)}:${week}`, (counts.get(`${localDow(day)}:${week}`) ?? 0) + c.count);
  }

  const lastDay = startOfLocalDay(windowEndMs);
  const weekCount = Math.round((startOfLocalWeek(windowEndMs) - firstMonday) / ONE_WEEK) + 1;
  const rows: GridRow[] = [];
  for (let dow = 0; dow < 7; dow++) {
    const cellsRow: GridCell[] = [];
    for (let w = 0; w < weekCount; w++) {
      const dayStart = addLocalDays(firstMonday, w * 7 + dow);
      cellsRow.push({
        bucket: dayStart,
        count: counts.get(`${dow}:${w}`) ?? 0,
        inRange: dayStart <= lastDay,
      });
    }
    rows.push({ label: DOW_LABELS[dow] ?? "", cells: cellsRow });
  }
  return { rows, cols: weekCount, columnLabels: monthAxisLabels(firstMonday, weekCount) };
}

function startOfLocalDay(ms: number): number {
  const d = new Date(ms);
  d.setHours(0, 0, 0, 0);
  return d.getTime();
}

function addLocalDays(dayStartMs: number, days: number): number {
  const d = new Date(dayStartMs);
  d.setDate(d.getDate() + days);
  d.setHours(0, 0, 0, 0);
  return d.getTime();
}

function startOfLocalWeek(ms: number): number {
  const day = startOfLocalDay(ms);
  return addLocalDays(day, -localDow(day));
}

function localDow(ms: number): number {
  return (new Date(ms).getDay() + 6) % 7;
}

function hourAxisLabels(): { index: number; label: string }[] {
  return [0, 6, 12, 18, 23].map((h) => ({ index: h, label: String(h).padStart(2, "0") }));
}

function rollingHourLabels(cells: GridCell[]): { index: number; label: string }[] {
  const labels: { index: number; label: string }[] = [];
  for (let i = 0; i < cells.length; i++) {
    const cell = cells[i];
    if (!cell) continue;
    const hour = new Date(cell.bucket).getHours();
    if (hour % 6 === 0) labels.push({ index: i, label: String(hour).padStart(2, "0") });
  }
  return labels;
}

function monthAxisLabels(
  firstMonday: number,
  weekCount: number,
): { index: number; label: string }[] {
  const labels: { index: number; label: string }[] = [];
  let lastMonth = -1;
  for (let w = 0; w < weekCount; w++) {
    const m = new Date(addLocalDays(firstMonday, w * 7)).getMonth();
    if (m !== lastMonth) {
      labels.push({ index: w, label: MONTH_LABELS[m] ?? "" });
      lastMonth = m;
    }
  }
  return labels;
}

function rowDateLabel(dayStartMs: number, mode: HeatmapMode): string {
  const d = new Date(dayStartMs);
  const month = MONTH_LABELS[d.getMonth()];
  const day = d.getDate();
  if (mode === "week") {
    return `${DOW_LABELS[(d.getDay() + 6) % 7]} ${day}`;
  }
  return `${month} ${day}`;
}

function formatBucket(bucketMs: number, bucketSizeMs: number): string {
  const d = new Date(bucketMs);
  const month = MONTH_LABELS[d.getMonth()];
  const day = d.getDate();
  const dow = DOW_LABELS[(d.getDay() + 6) % 7];
  const year = d.getFullYear();
  const yearNow = new Date().getFullYear();
  const yearSuffix = year === yearNow ? "" : ` ${year}`;
  if (bucketSizeMs >= ONE_DAY) {
    return `${dow} ${month} ${day}${yearSuffix}`;
  }
  const hour = String(d.getHours()).padStart(2, "0");
  return `${dow} ${month} ${day}${yearSuffix} ${hour}:00`;
}

function subtitleFor(mode: HeatmapMode): string {
  switch (mode) {
    case "day":
      return "Articles per hour, last 24 hours";
    case "week":
      return "Articles per hour, last 7 days";
    case "month":
      return "Articles per hour, last 30 days";
    case "year":
      return "Articles per day, last year";
  }
}

function emptyMessageFor(mode: HeatmapMode): string {
  switch (mode) {
    case "day":
      return "No activity in the last 24 hours yet.";
    case "week":
      return "No activity in the last 7 days yet.";
    case "month":
      return "No activity in the last 30 days yet.";
    case "year":
      return "No activity in the last year yet.";
  }
}

function cellColor(intensity: number): string {
  if (intensity <= 0) return "color-mix(in srgb, var(--color-base-content) 4%, transparent)";
  const eased = Math.pow(Math.min(1, intensity), 0.6);
  const alpha = 0.15 + eased * 0.75;
  return `color-mix(in srgb, var(--color-success) ${(alpha * 100).toFixed(0)}%, transparent)`;
}
