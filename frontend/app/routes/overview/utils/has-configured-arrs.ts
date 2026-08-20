export function hasConfiguredArrs(configValue?: string): boolean {
    if (!configValue) return false;

    try {
        const config: unknown = JSON.parse(configValue);
        if (typeof config !== "object" || config === null) return false;

        const radarr = "RadarrInstances" in config && Array.isArray(config.RadarrInstances)
            ? config.RadarrInstances
            : [];
        const sonarr = "SonarrInstances" in config && Array.isArray(config.SonarrInstances)
            ? config.SonarrInstances
            : [];

        return [...radarr, ...sonarr].some((instance) =>
            typeof instance === "object"
            && instance !== null
            && (!("Enabled" in instance) || instance.Enabled !== false));
    } catch {
        return false;
    }
}
