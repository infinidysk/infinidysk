import styles from "./usenet.module.css"
import { type Dispatch, type SetStateAction, useState, useCallback, useEffect, useMemo } from "react";
import { Button } from "react-bootstrap";
import { receiveMessage } from "~/utils/websocket-util";
import type { GetProviderUsageStatsResponse, ProviderUsageStatDaily } from "~/clients/backend-client.server";

const websocketTopics = {'cxs': 'state', 'pus': 'state'};

type UsenetSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
    providerUsageStats: GetProviderUsageStatsResponse
};

enum ProviderType {
    Disabled = 0,
    Pooled = 1,
    BackupAndStats = 2,
    BackupOnly = 3,
}

type ConnectionDetails = {
    Id: string;
    Type: ProviderType;
    Host: string;
    Port: number;
    UseSsl: boolean;
    User: string;
    Pass: string;
    MaxConnections: number;
    MonthlyQuotaGb?: number;
};

type ProviderUsage = {
    host: string;
    bytesDownloaded: number;
    articlesNotFound: number;
    isTripped: boolean;
    lastUsedAt: string | null;
};

type ConnectionCounts = {
    live: number;
    active: number;
    max: number;
}

type UsenetProviderConfig = {
    Providers: ConnectionDetails[];
};

const PROVIDER_TYPE_LABELS: Record<ProviderType, string> = {
    [ProviderType.Disabled]: "Disabled",
    [ProviderType.Pooled]: "Pool Connections",
    [ProviderType.BackupAndStats]: "Backup & Health Checks",
    [ProviderType.BackupOnly]: "Backup Only",
};

function parseProviderConfig(jsonString: string): UsenetProviderConfig {
    try {
        if (!jsonString || jsonString.trim() === "") {
            return { Providers: [] };
        }
        return JSON.parse(jsonString);
    } catch {
        return { Providers: [] };
    }
}

function serializeProviderConfig(config: UsenetProviderConfig): string {
    return JSON.stringify(config);
}

function formatBytes(bytes: number): string {
    if (bytes <= 0) return "0 B";
    const units = ["B", "KB", "MB", "GB", "TB", "PB"];
    const exponent = Math.min(units.length - 1, Math.floor(Math.log(bytes) / Math.log(1024)));
    const value = bytes / Math.pow(1024, exponent);
    return `${value.toFixed(exponent === 0 || value >= 100 ? 0 : 1)} ${units[exponent]}`;
}

function formatRelativeTime(isoString: string | null): string {
    if (!isoString) return "Never";
    const diffSeconds = Math.max(0, Math.floor((Date.now() - new Date(isoString).getTime()) / 1000));
    if (diffSeconds < 60) return "Just now";
    const diffMinutes = Math.floor(diffSeconds / 60);
    if (diffMinutes < 60) return `${diffMinutes}m ago`;
    const diffHours = Math.floor(diffMinutes / 60);
    if (diffHours < 24) return `${diffHours}h ago`;
    return `${Math.floor(diffHours / 24)}d ago`;
}

// Builds a fixed 30-day (oldest -> newest) series for one provider, filling gaps left by
// days with no buckets (nothing downloaded that day) with zeroes.
function buildDailySeries(dailyBuckets: ProviderUsageStatDaily[], providerId: string) {
    const byDate = new Map<string, { bytes: number; notFound: number }>();
    for (const bucket of dailyBuckets) {
        if (bucket.providerId !== providerId) continue;
        const dateKey = bucket.dateStartInclusive.slice(0, 10);
        byDate.set(dateKey, { bytes: bucket.bytesDownloaded, notFound: bucket.articlesNotFoundCount });
    }

    const today = new Date();
    const days: { date: string; bytes: number; notFound: number }[] = [];
    for (let i = 29; i >= 0; i--) {
        const date = new Date(Date.UTC(today.getUTCFullYear(), today.getUTCMonth(), today.getUTCDate() - i));
        const dateKey = date.toISOString().slice(0, 10);
        const entry = byDate.get(dateKey);
        days.push({ date: dateKey, bytes: entry?.bytes ?? 0, notFound: entry?.notFound ?? 0 });
    }
    return days;
}

function sumMonthToDateBytes(dailyBuckets: ProviderUsageStatDaily[], providerId: string): number {
    const now = new Date();
    const monthStart = Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), 1);
    return dailyBuckets
        .filter(b => b.providerId === providerId && new Date(b.dateStartInclusive).getTime() >= monthStart)
        .reduce((sum, b) => sum + b.bytesDownloaded, 0);
}

export function UsenetSettings({ config, setNewConfig, providerUsageStats }: UsenetSettingsProps) {
    // state
    const [showModal, setShowModal] = useState(false);
    const [editingIndex, setEditingIndex] = useState<number | null>(null);
    const [connections, setConnections] = useState<{[index: number]: ConnectionCounts}>({});
    const providerConfig = useMemo(() => parseProviderConfig(config["usenet.providers"]), [config]);

    const initialUsage = useMemo(() => {
        const map: Record<string, ProviderUsage> = {};
        for (const stat of providerUsageStats.totals) {
            map[stat.providerId] = {
                host: stat.providerHost,
                bytesDownloaded: stat.bytesDownloaded,
                articlesNotFound: stat.articlesNotFoundCount,
                isTripped: false,
                lastUsedAt: stat.lastUpdatedAt,
            };
        }
        return map;
    }, [providerUsageStats]);
    const [usage, setUsage] = useState(initialUsage);

    // handlers
    const handleAddProvider = useCallback(() => {
        setEditingIndex(null);
        setShowModal(true);
    }, []);

    const handleEditProvider = useCallback((index: number) => {
        setEditingIndex(index);
        setShowModal(true);
    }, []);

    const handleDeleteProvider = useCallback((index: number) => {
        const newProviderConfig = { ...providerConfig };
        newProviderConfig.Providers = providerConfig.Providers.filter((_, i) => i !== index);
        setNewConfig({ ...config, "usenet.providers": serializeProviderConfig(newProviderConfig) });
    }, [config, providerConfig, setNewConfig]);

    const handleCloseModal = useCallback(() => {
        setShowModal(false);
        setEditingIndex(null);
    }, []);

    const handleSaveProvider = useCallback((provider: ConnectionDetails) => {
        const newProviderConfig = { ...providerConfig };
        if (editingIndex !== null) {
            newProviderConfig.Providers[editingIndex] = provider;
        } else {
            newProviderConfig.Providers.push(provider);
        }
        setNewConfig({ ...config, "usenet.providers": serializeProviderConfig(newProviderConfig) });
        handleCloseModal();
    }, [config, providerConfig, editingIndex, setNewConfig, handleCloseModal]);

    const handleConnectionsMessage = useCallback((message: string) => {
        const parts = (message || "0|0|0|0|1|0").split("|");
        const [index, live, idle, _0, _1, _2] = parts.map((x: any) => Number(x));
        if (showModal) return;
        if (index >= providerConfig.Providers.length) return;
        setConnections(prev => ({...prev, [index]: {
            active: live - idle,
            live: live,
            max: providerConfig.Providers[index]?.MaxConnections || 1
        }}));
    }, [setConnections]);

    const handleProviderUsageMessage = useCallback((message: string) => {
        try {
            const entries: Array<{
                id: string; host: string; bytesDownloaded: number;
                articlesNotFound: number; isTripped: boolean; lastUsedAt: string | null;
            }> = JSON.parse(message || "[]");
            setUsage(prev => {
                const next = { ...prev };
                for (const entry of entries) {
                    next[entry.id] = {
                        host: entry.host,
                        bytesDownloaded: entry.bytesDownloaded,
                        articlesNotFound: entry.articlesNotFound,
                        isTripped: entry.isTripped,
                        lastUsedAt: entry.lastUsedAt,
                    };
                }
                return next;
            });
        } catch {
            // ignore malformed message
        }
    }, [setUsage]);

    const onWebsocketMessage = useCallback((topic: string, message: string) => {
        if (topic === 'cxs') handleConnectionsMessage(message);
        else if (topic === 'pus') handleProviderUsageMessage(message);
    }, [handleConnectionsMessage, handleProviderUsageMessage]);

    // effects
    useEffect(() => {
        let ws: WebSocket;
        let disposed = false;
        function connect() {
            ws = new WebSocket(window.location.origin.replace(/^http/, 'ws'));
            ws.onmessage = receiveMessage(onWebsocketMessage);
            ws.onopen = () => ws.send(JSON.stringify(websocketTopics));
            ws.onerror = () => { ws.close() };
            ws.onclose = onClose;
            return () => { disposed = true; ws.close(); }
        }
        function onClose(e: CloseEvent) {
            !disposed && setTimeout(() => connect(), 1000);
            setConnections({});
        }
        return connect();
    }, [setConnections, onWebsocketMessage]);

    // view
    return (
        <div className={styles.container}>
            <div style={{
                background: "red",
                color: "white",
                fontSize: "24px",
                fontWeight: "bold",
                padding: "16px",
                textAlign: "center",
            }}>
                TEST MARKER — PROVIDER STATS BUILD IS LIVE
            </div>
            <div className={styles.section}>
                <div className={styles.sectionHeader}>
                    <div>Usenet Providers</div>
                    <Button variant="primary" size="sm" onClick={handleAddProvider}>
                        Add
                    </Button>
                </div>
                {providerConfig.Providers.length === 0 ? (
                    <p className={styles.alertMessage}>
                        No Usenet providers configured.
                        Click on the "Add" button to get started.
                    </p>
                ) : (
                    <div className={styles["providers-grid"]}>
                        {providerConfig.Providers.map((provider, index) => (
                            <div key={index} className={styles["provider-card"]}>
                                <div className={styles["provider-card-inner"]}>
                                    <div className={styles["provider-header"]}>
                                        <div className={styles["provider-header-content"]}>
                                            <div className={styles["provider-host"]}>
                                                {provider.Host}
                                            </div>
                                            <div className={styles["provider-port"]}>
                                                Port {provider.Port}
                                            </div>
                                        </div>
                                        <div className={styles["provider-header-actions"]}>
                                            <button
                                                className={styles["header-action-button"]}
                                                onClick={() => handleEditProvider(index)}
                                                title="Edit Provider"
                                            >
                                                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                                    <path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7" />
                                                    <path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z" />
                                                </svg>
                                            </button>
                                            <button
                                                className={`${styles["header-action-button"]} ${styles["delete"]}`}
                                                onClick={() => handleDeleteProvider(index)}
                                                title="Delete Provider"
                                            >
                                                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                                    <polyline points="3 6 5 6 21 6" />
                                                    <path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" />
                                                </svg>
                                            </button>
                                        </div>
                                    </div>

                                    <div className={styles["provider-details"]}>
                                        <div className={styles["provider-detail-row"]}>

                                            <div className={styles["provider-detail-item"]}>
                                                <div className={styles["provider-detail-icon"]}>
                                                    <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                                        <path d="M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2" />
                                                        <circle cx="12" cy="7" r="4" />
                                                    </svg>
                                                </div>
                                                <div className={styles["provider-detail-content"]}>
                                                    <span className={styles["provider-detail-label"]}>Username</span>
                                                    <span className={styles["provider-detail-value"]}>{provider.User}</span>
                                                </div>
                                            </div>

                                            <div className={styles["provider-detail-item"]}>
                                                {connections[index] && (
                                                    <div className={styles["connection-bar"]}>
                                                        <div
                                                            className={styles["connection-bar-live"]}
                                                            style={{ width: `${100 * (connections[index].live / connections[index].max)}%` }}
                                                        />
                                                        <div
                                                            className={styles["connection-bar-active"]}
                                                            style={{ width: `${100 * (connections[index].active / connections[index].max)}%` }}
                                                        />
                                                    </div>
                                                )}
                                                <div className={styles["provider-detail-icon"]}>
                                                    <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                                        <path d="M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z" />
                                                    </svg>
                                                </div>
                                                <div className={styles["provider-detail-content"]}>
                                                    <span className={styles["provider-detail-label"]}>Max Connections</span>
                                                    <span className={styles["provider-detail-value"]}>{provider.MaxConnections}</span>
                                                </div>
                                            </div>

                                            <div className={styles["provider-detail-item"]}>
                                                <div className={styles["provider-detail-icon"]}>
                                                    {provider.UseSsl ? (
                                                        // Closed lock icon
                                                        <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                                            <rect x="5" y="11" width="14" height="11" rx="2" ry="2" />
                                                            <path d="M7 11V7a5 5 0 0 1 10 0v4" />
                                                            <circle cx="12" cy="16" r="1" fill="currentColor" />
                                                        </svg>
                                                    ) : (
                                                        // Open lock icon
                                                        <svg width="13" height="13" viewBox="0 -2 24 26" fill="none" stroke="currentColor" strokeWidth="2">
                                                            <rect x="5" y="11" width="14" height="11" rx="2" ry="2" />
                                                            <path d="M7 11V4a5 5 0 0 1 9.9 1" />
                                                            <circle cx="12" cy="16" r="1" fill="currentColor" />
                                                        </svg>
                                                    )}
                                                </div>
                                                <div className={styles["provider-detail-content"]}>
                                                    <span className={styles["provider-detail-label"]}>Security</span>
                                                    <span className={styles["provider-detail-value"]}>
                                                        {provider.UseSsl ? "SSL Enabled" : "No SSL"}
                                                    </span>
                                                </div>
                                            </div>

                                            <div className={styles["provider-detail-item"]}>
                                                <div className={styles["provider-detail-icon"]}>
                                                    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.3">
                                                        <text x="12" y="9" fontSize="10" fill="currentColor" textAnchor="middle" fontWeight="600">1</text>
                                                        <text x="6" y="21" fontSize="10" fill="currentColor" textAnchor="middle" fontWeight="600">2</text>
                                                        <text x="18" y="21" fontSize="10" fill="currentColor" textAnchor="middle" fontWeight="600">3</text>
                                                    </svg>
                                                </div>
                                                <div className={styles["provider-detail-content"]}>
                                                    <span className={styles["provider-detail-label"]}>Behavior</span>
                                                    <span className={styles["provider-detail-value"]}>{PROVIDER_TYPE_LABELS[provider.Type]}</span>
                                                </div>
                                            </div>

                                            <div className={styles["provider-detail-item"]}>
                                                <div className={styles["provider-detail-icon"]}>
                                                    <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                                        <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" />
                                                        <polyline points="7 10 12 15 17 10" />
                                                        <line x1="12" y1="15" x2="12" y2="3" />
                                                    </svg>
                                                </div>
                                                <div className={styles["provider-detail-content"]}>
                                                    <span className={styles["provider-detail-label"]}>Downloaded</span>
                                                    <span className={styles["provider-detail-value"]}>
                                                        {formatBytes(usage[provider.Id]?.bytesDownloaded ?? 0)}
                                                    </span>
                                                </div>
                                            </div>

                                            <div className={styles["provider-detail-item"]}>
                                                <div className={styles["provider-detail-icon"]}>
                                                    <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                                        <circle cx="12" cy="12" r="10" />
                                                        <line x1="15" y1="9" x2="9" y2="15" />
                                                        <line x1="9" y1="9" x2="15" y2="15" />
                                                    </svg>
                                                </div>
                                                <div className={styles["provider-detail-content"]}>
                                                    <span className={styles["provider-detail-label"]}>Not Found</span>
                                                    <span className={styles["provider-detail-value"]}>
                                                        {(usage[provider.Id]?.articlesNotFound ?? 0).toLocaleString()}
                                                    </span>
                                                </div>
                                            </div>

                                            <div className={styles["provider-detail-item"]}>
                                                <div className={styles["provider-detail-icon"]}>
                                                    <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                                        <circle cx="12" cy="12" r="10" />
                                                        <polyline points="12 6 12 12 16 14" />
                                                    </svg>
                                                </div>
                                                <div className={styles["provider-detail-content"]}>
                                                    <span className={styles["provider-detail-label"]}>Last Used</span>
                                                    <span className={styles["provider-detail-value"]}>
                                                        {formatRelativeTime(usage[provider.Id]?.lastUsedAt ?? null)}
                                                    </span>
                                                </div>
                                            </div>

                                            <div className={styles["provider-detail-item"]}>
                                                <div
                                                    className={`${styles["provider-detail-icon"]} ${usage[provider.Id]?.isTripped ? styles["status-icon-danger"] : styles["status-icon-ok"]}`}
                                                >
                                                    <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                                                        <path d="M12 2 4 6v6c0 5 3.5 8.5 8 10 4.5-1.5 8-5 8-10V6z" />
                                                    </svg>
                                                </div>
                                                <div className={styles["provider-detail-content"]}>
                                                    <span className={styles["provider-detail-label"]}>Circuit Breaker</span>
                                                    <span className={styles["provider-detail-value"]}>
                                                        {usage[provider.Id]?.isTripped ? "Paused (errors)" : "Active"}
                                                    </span>
                                                </div>
                                            </div>

                                        </div>

                                        {typeof provider.MonthlyQuotaGb === "number" && provider.MonthlyQuotaGb > 0 && (
                                            <MonthlyQuotaBar
                                                usedBytes={sumMonthToDateBytes(providerUsageStats.dailyBuckets, provider.Id)}
                                                quotaGb={provider.MonthlyQuotaGb}
                                            />
                                        )}

                                        <UsageSparkline days={buildDailySeries(providerUsageStats.dailyBuckets, provider.Id)} />
                                    </div>
                                </div>
                            </div>
                        ))}
                    </div>
                )}
            </div>

            <ProviderModal
                show={showModal}
                provider={editingIndex !== null ? providerConfig.Providers[editingIndex] : null}
                onClose={handleCloseModal}
                onSave={handleSaveProvider}
            />
        </div>
    );
}

type MonthlyQuotaBarProps = {
    usedBytes: number;
    quotaGb: number;
};

function MonthlyQuotaBar({ usedBytes, quotaGb }: MonthlyQuotaBarProps) {
    const quotaBytes = quotaGb * 1024 * 1024 * 1024;
    const percent = quotaBytes > 0 ? (usedBytes / quotaBytes) * 100 : 0;
    const fillClass = percent >= 100
        ? styles["quota-bar-fill-danger"]
        : percent >= 80
            ? styles["quota-bar-fill-warning"]
            : styles["quota-bar-fill-ok"];

    return (
        <div className={styles["quota-bar-wrapper"]}>
            <div className={styles["quota-bar-label"]}>
                <span>{formatBytes(usedBytes)} of {quotaGb} GB this month</span>
                <span>{Math.round(percent)}%</span>
            </div>
            <div className={styles["quota-bar-track"]}>
                <div
                    className={`${styles["quota-bar-fill"]} ${fillClass}`}
                    style={{ width: `${Math.min(100, percent)}%` }}
                />
            </div>
        </div>
    );
}

type UsageSparklineProps = {
    days: { date: string; bytes: number; notFound: number }[];
};

// A compact 30-day bytes-downloaded bar chart. Days with "not found" events are
// marked with a small dot above the bar. Consistent with the rest of this file's
// hand-rolled UI (e.g. the connection-bar) - no charting library involved.
function UsageSparkline({ days }: UsageSparklineProps) {
    const width = 300;
    const height = 36;
    const gap = 2;
    const barWidth = (width - gap * (days.length - 1)) / days.length;
    const maxBytes = Math.max(1, ...days.map(d => d.bytes));

    return (
        <svg
            className={styles["usage-sparkline"]}
            width="100%"
            height={height}
            viewBox={`0 0 ${width} ${height}`}
            preserveAspectRatio="none"
            role="img"
            aria-label="Bytes downloaded over the last 30 days"
        >
            {days.map((day, i) => {
                const barHeight = day.bytes > 0 ? Math.max(2, (day.bytes / maxBytes) * (height - 6)) : 1;
                const x = i * (barWidth + gap);
                const y = height - barHeight;
                return (
                    <g key={day.date}>
                        <rect
                            x={x}
                            y={y}
                            width={barWidth}
                            height={barHeight}
                            rx={1}
                            className={styles["sparkline-bar"]}
                        />
                        {day.notFound > 0 && (
                            <circle cx={x + barWidth / 2} cy={3} r={1.5} className={styles["sparkline-marker"]} />
                        )}
                        <title>
                            {`${day.date}: ${formatBytes(day.bytes)} downloaded`}
                            {day.notFound > 0 ? `, ${day.notFound} not found` : ""}
                        </title>
                    </g>
                );
            })}
        </svg>
    );
}

type ProviderModalProps = {
    show: boolean;
    provider: ConnectionDetails | null;
    onClose: () => void;
    onSave: (provider: ConnectionDetails) => void;
};

function ProviderModal({ show, provider, onClose, onSave }: ProviderModalProps) {
    const [host, setHost] = useState(provider?.Host || "");
    const [port, setPort] = useState(provider?.Port?.toString() || "");
    const [useSsl, setUseSsl] = useState(provider?.UseSsl ?? true);
    const [user, setUser] = useState(provider?.User || "");
    const [pass, setPass] = useState(provider?.Pass || "");
    const [maxConnections, setMaxConnections] = useState(provider?.MaxConnections?.toString() || "");
    const [monthlyQuotaGb, setMonthlyQuotaGb] = useState(provider?.MonthlyQuotaGb?.toString() || "");
    const [type, setType] = useState<ProviderType>(provider?.Type ?? ProviderType.Pooled);
    const [isTestingConnection, setIsTestingConnection] = useState(false);
    const [connectionTested, setConnectionTested] = useState(false);
    const [testError, setTestError] = useState<string | null>(null);

    // Reset form when modal opens or provider changes
    useEffect(() => {
        if (show) {
            setHost(provider?.Host || "");
            setPort(provider?.Port?.toString() || "");
            setUseSsl(provider?.UseSsl ?? true);
            setUser(provider?.User || "");
            setPass(provider?.Pass || "");
            setMaxConnections(provider?.MaxConnections?.toString() || "");
            setMonthlyQuotaGb(provider?.MonthlyQuotaGb?.toString() || "");
            setType(provider?.Type ?? ProviderType.Pooled);
            setConnectionTested(false);
            setTestError(null);
        }
    }, [show, provider]);

    // Handle Escape key to close modal
    useEffect(() => {
        const handleEscape = (e: KeyboardEvent) => {
            if (e.key === 'Escape' && show) {
                onClose();
            }
        };

        if (show) {
            document.addEventListener('keydown', handleEscape);
            return () => document.removeEventListener('keydown', handleEscape);
        }
    }, [show, onClose]);

    const handleTestConnection = useCallback(async () => {
        setIsTestingConnection(true);
        setTestError(null);

        try {
            const formData = new FormData();
            formData.append('host', host);
            formData.append('port', port);
            formData.append('use-ssl', useSsl.toString());
            formData.append('user', user);
            formData.append('pass', pass);

            const response = await fetch('/api/test-usenet-connection', {
                method: 'POST',
                body: formData,
            });

            if (response.ok) {
                const data = await response.json();
                if (data.connected) {
                    setConnectionTested(true);
                    setTestError(null);
                } else {
                    setTestError("Connection test failed");
                }
            } else {
                setTestError("Failed to test connection");
            }
        } catch (error) {
            setTestError("Network error: " + (error instanceof Error ? error.message : "Unknown error"));
        } finally {
            setIsTestingConnection(false);
        }
    }, [host, port, useSsl, user, pass]);

    const handleSave = useCallback(() => {
        const trimmedQuota = monthlyQuotaGb.trim();
        onSave({
            Id: provider?.Id || crypto.randomUUID(),
            Type: type,
            Host: host,
            Port: parseInt(port, 10),
            UseSsl: useSsl,
            User: user,
            Pass: pass,
            MaxConnections: parseInt(maxConnections, 10),
            MonthlyQuotaGb: trimmedQuota === "" ? undefined : Number(trimmedQuota),
        });
    }, [provider, type, host, port, useSsl, user, pass, maxConnections, monthlyQuotaGb, onSave]);

    const handleOverlayClick = useCallback((e: React.MouseEvent) => {
        if (e.target === e.currentTarget) {
            onClose();
        }
    }, [onClose]);

    const isFormValid = host.trim() !== ""
        && isPositiveInteger(port)
        && user.trim() !== ""
        && pass.trim() !== ""
        && isPositiveInteger(maxConnections)
        && (monthlyQuotaGb.trim() === "" || isPositiveNumber(monthlyQuotaGb));

    const canSave = isFormValid && (connectionTested || type == ProviderType.Disabled);

    if (!show) return null;

    return (
        <div className={styles["modal-overlay"]} onClick={handleOverlayClick}>
            <div className={styles["modal-container"]}>
                <div className={styles["modal-header"]}>
                    <h2 className={styles["modal-title"]}>
                        {provider ? "Edit Provider" : "Add Provider"}
                    </h2>
                    <button className={styles["modal-close"]} onClick={onClose} aria-label="Close">
                        ×
                    </button>
                </div>

                <div className={styles["modal-body"]}>
                    <div className={styles["form-grid"]}>
                        <div className={styles["form-group"]}>
                            <label htmlFor="provider-host" className={styles["form-label"]}>
                                Host
                            </label>
                            <input
                                type="text"
                                id="provider-host"
                                className={styles["form-input"]}
                                placeholder="news.provider.com"
                                value={host}
                                onChange={(e) => {
                                    setHost(e.target.value);
                                    setConnectionTested(false);
                                }}
                            />
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="provider-port" className={styles["form-label"]}>
                                Port
                            </label>
                            <input
                                type="text"
                                id="provider-port"
                                className={`${styles["form-input"]} ${!isPositiveInteger(port) && port !== "" ? styles.error : ""}`}
                                placeholder="563"
                                value={port}
                                onChange={(e) => {
                                    setPort(e.target.value);
                                    setConnectionTested(false);
                                }}
                            />
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="provider-user" className={styles["form-label"]}>
                                Username
                            </label>
                            <input
                                type="text"
                                id="provider-user"
                                className={styles["form-input"]}
                                placeholder="username"
                                value={user}
                                onChange={(e) => {
                                    setUser(e.target.value);
                                    setConnectionTested(false);
                                }}
                            />
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="provider-pass" className={styles["form-label"]}>
                                Password
                            </label>
                            <input
                                type="password"
                                id="provider-pass"
                                className={styles["form-input"]}
                                placeholder="password"
                                value={pass}
                                onChange={(e) => {
                                    setPass(e.target.value);
                                    setConnectionTested(false);
                                }}
                            />
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="provider-max-connections" className={styles["form-label"]}>
                                Max Connections
                            </label>
                            <input
                                type="text"
                                id="provider-max-connections"
                                className={`${styles["form-input"]} ${!isPositiveInteger(maxConnections) && maxConnections !== "" ? styles.error : ""}`}
                                placeholder="20"
                                value={maxConnections}
                                onChange={(e) => setMaxConnections(e.target.value)}
                            />
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="provider-monthly-quota" className={styles["form-label"]}>
                                Monthly Data Limit (GB, optional)
                            </label>
                            <input
                                type="text"
                                id="provider-monthly-quota"
                                className={`${styles["form-input"]} ${!isPositiveNumber(monthlyQuotaGb) && monthlyQuotaGb !== "" ? styles.error : ""}`}
                                placeholder="No limit"
                                value={monthlyQuotaGb}
                                onChange={(e) => setMonthlyQuotaGb(e.target.value)}
                            />
                        </div>

                        <div className={styles["form-group"]}>
                            <label htmlFor="provider-type" className={styles["form-label"]}>
                                Type
                            </label>
                            <select
                                id="provider-type"
                                className={styles["form-select"]}
                                value={type}
                                onChange={(e) => setType(parseInt(e.target.value, 10) as ProviderType)}
                            >
                                <option value={ProviderType.Disabled}>Disabled</option>
                                <option value={ProviderType.Pooled}>Pool Connections</option>
                                <option value={ProviderType.BackupOnly}>Backup Only</option>
                            </select>
                        </div>

                        <div className={`${styles["form-group"]} ${styles["full-width"]}`}>
                            <div className={styles["form-checkbox-wrapper"]}>
                                <input
                                    type="checkbox"
                                    id="provider-ssl"
                                    className={styles["form-checkbox"]}
                                    checked={useSsl}
                                    onChange={(e) => {
                                        setUseSsl(e.target.checked);
                                        setConnectionTested(false);
                                    }}
                                />
                                <label htmlFor="provider-ssl" className={styles["form-checkbox-label"]}>
                                    Use SSL
                                </label>
                            </div>
                        </div>
                    </div>

                    {testError && (
                        <div className={`${styles.alert} ${styles["alert-danger"]}`} style={{ marginTop: '16px' }}>
                            {testError}
                        </div>
                    )}

                    {connectionTested && (
                        <div className={`${styles.alert} ${styles["alert-success"]}`} style={{ marginTop: '16px' }}>
                            Connection test successful!
                        </div>
                    )}
                </div>

                <div className={styles["modal-footer"]}>
                    <div className={styles["modal-footer-left"]}></div>
                    <div className={styles["modal-footer-right"]}>
                        <Button variant="secondary" onClick={onClose}>
                            Cancel
                        </Button>
                        {!canSave ? (
                            <Button
                                variant="primary"
                                onClick={handleTestConnection}
                                disabled={!isFormValid || isTestingConnection}
                            >
                                {isTestingConnection ? "Testing..." : "Test Connection"}
                            </Button>
                        ) : (
                            <Button variant="primary" onClick={handleSave} disabled={!canSave}>
                                Save Provider
                            </Button>
                        )}
                    </div>
                </div>
            </div>
        </div>
    );
}

export function isUsenetSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["usenet.providers"] !== newConfig["usenet.providers"]
}

export function isPositiveInteger(value: string) {
    const num = Number(value);
    return Number.isInteger(num) && num > 0 && value.trim() === num.toString();
}

export function isPositiveNumber(value: string) {
    const num = Number(value);
    return value.trim() !== "" && Number.isFinite(num) && num > 0;
}