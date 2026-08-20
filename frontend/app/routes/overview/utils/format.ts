// Decimal (SI) bytes — KB/MB/GB at base 1000. Matches what people mean by
// "MB/s" in everyday usage (and what sabnzbd / NZBGet / hosters all use).
export function formatBytes(bytes: number): string {
    if (!isFinite(bytes) || bytes <= 0) return "0 B";
    const units = ["B", "KB", "MB", "GB", "TB", "PB"];
    let i = 0;
    let v = bytes;
    while (v >= 1000 && i < units.length - 1) { v /= 1000; i++; }
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

export function formatDurationMs(ms: number | null | undefined): string {
    if (ms == null || !Number.isFinite(ms) || ms < 0) return "—";
    if (ms < 60_000) {
        const seconds = ms / 1000;
        return seconds < 10 ? `${seconds.toFixed(1)}s` : `${Math.round(seconds)}s`;
    }
    const totalSeconds = Math.round(ms / 1000);
    const hours = Math.floor(totalSeconds / 3600);
    const minutes = Math.floor((totalSeconds % 3600) / 60);
    const seconds = totalSeconds % 60;
    if (hours > 0) return minutes > 0 ? `${hours}h ${minutes}m` : `${hours}h`;
    return seconds > 0 ? `${minutes}m ${seconds}s` : `${minutes}m`;
}

export function formatTimeAgo(ms: number | null | undefined, now = Date.now()): string {
    if (ms == null || !Number.isFinite(ms)) return "—";
    const age = Math.max(0, Math.round((now - ms) / 1000));
    if (age < 60) return `${age}s ago`;
    if (age < 3600) return `${Math.floor(age / 60)}m ago`;
    if (age < 86400) return `${Math.floor(age / 3600)}h ago`;
    return `${Math.floor(age / 86400)}d ago`;
}
