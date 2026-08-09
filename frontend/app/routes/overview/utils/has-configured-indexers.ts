export function hasConfiguredIndexers(configValue?: string): boolean {
    if (!configValue) return false;

    try {
        const config: unknown = JSON.parse(configValue);
        return typeof config === "object"
            && config !== null
            && "Indexers" in config
            && Array.isArray(config.Indexers)
            && config.Indexers.length > 0;
    } catch {
        return false;
    }
}
