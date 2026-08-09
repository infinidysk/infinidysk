// Decimal (SI) bytes — KB/MB/GB at base 1000. Matches what people mean by
// "MB/s" in everyday usage (and what sabnzbd / NZBGet / hosters all use).
export function formatBytes(bytes: number): string {
  if (!isFinite(bytes) || bytes <= 0) return "0 B";
  const units = ["B", "KB", "MB", "GB", "TB", "PB"];
  let i = 0;
  let v = bytes;
  while (v >= 1000 && i < units.length - 1) {
    v /= 1000;
    i++;
  }
  return v >= 100 ? `${v.toFixed(0)} ${units[i]}` : `${v.toFixed(1)} ${units[i]}`;
}

export function formatSpeed(mbPerSec: number | null | undefined): string {
  return mbPerSec == null || !isFinite(mbPerSec) ? "—" : mbPerSec.toFixed(1);
}

export function formatNumber(n: number): string {
  return n.toLocaleString();
}

export function formatPercent(p: number, digits = 1): string {
  return `${p.toFixed(digits)}%`;
}
