export function hasConfiguredIndexers(configValue?: string): boolean {
    if (!configValue) return false;

    try {
        const config = JSON.parse(configValue);
        return Array.isArray(config?.Indexers) && config.Indexers.length > 0;
    } catch {
        return false;
    }
}
