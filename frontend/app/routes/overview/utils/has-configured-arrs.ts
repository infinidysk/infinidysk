export function hasConfiguredArrs(configValue?: string): boolean {
    if (!configValue) return false;

    try {
        const config: unknown = JSON.parse(configValue);
        if (typeof config !== "object" || config === null) return false;

        const record = config as Record<string, unknown>;
        const radarr: unknown[] = Array.isArray(record["RadarrInstances"]) ? record["RadarrInstances"] : [];
        const sonarr: unknown[] = Array.isArray(record["SonarrInstances"]) ? record["SonarrInstances"] : [];

        return [...radarr, ...sonarr].some((instance) => {
            if (typeof instance !== "object" || instance === null) return false;
            const arrInstance = instance as Record<string, unknown>;
            return !("Enabled" in arrInstance) || arrInstance["Enabled"] !== false;
        });
    } catch {
        return false;
    }
}
