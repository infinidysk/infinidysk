const units = ["B", "KB", "MB", "GB", "TB"];

/** `compact` drops the decimal from 10 up (83 GB, but still 1.4 TB). */
export function bytes(n: number, compact = false): string {
  let i = 0;
  while (n >= 1000 && i < units.length - 1) {
    n /= 1000;
    i++;
  }
  return `${n.toFixed(i === 0 || (compact && n >= 9.95) ? 0 : 1)} ${units[i]}`;
}

/** "1 result", "1,234 results": the count with `toLocaleString()` and the noun in singular or plural. */
export function count(n: number, one: string, many = `${one}s`): string {
  return `${n.toLocaleString()} ${n === 1 ? one : many}`;
}

const relative = new Intl.RelativeTimeFormat(undefined, { numeric: "auto" });
const steps: [Intl.RelativeTimeFormatUnit, number][] = [
  ["second", 60],
  ["minute", 60],
  ["hour", 24],
  ["day", 30],
  ["month", 12],
  ["year", Infinity],
];

/** "3 minutes ago" for Unix milliseconds. */
export function ago(ms: number): string {
  let delta = (ms - Date.now()) / 1000;
  for (const [unit, size] of steps) {
    if (Math.abs(delta) < size) return relative.format(Math.round(delta), unit);
    delta /= size;
  }
  return "";
}

export function stamp(ms: number): string {
  return new Date(ms).toLocaleString();
}

/** "Oct 23, 2026, 3:52 PM": the visible date format in settings. */
export function day(ms: number): string {
  return new Date(ms).toLocaleString([], { dateStyle: "medium", timeStyle: "short" });
}

export function duration(seconds: number): string {
  if (seconds < 60) return `${seconds} s`;
  const m = Math.floor(seconds / 60);
  return m < 60 ? `${m} min ${seconds % 60} s` : `${Math.floor(m / 60)} h ${m % 60} min`;
}
