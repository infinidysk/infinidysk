import { Form, InputGroup } from "react-bootstrap";
import styles from "./cache.module.css"
import { type Dispatch, type SetStateAction } from "react";
import { className } from "~/utils/styling";
import { isPositiveInteger } from "../usenet/usenet";

type CacheSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

export function CacheSettings({ config, setNewConfig }: CacheSettingsProps) {
    const isEnabled = config["cache.prefetch-enabled"] === "true";

    return (
        <div className={styles.container}>
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="checkbox"
                    id="prefetch-enabled-checkbox"
                    aria-describedby="prefetch-enabled-help"
                    label={`Enable Episode Prefetch Cache`}
                    checked={isEnabled}
                    onChange={e => setNewConfig({ ...config, "cache.prefetch-enabled": "" + e.target.checked })} />
                <Form.Text id="prefetch-enabled-help" muted>
                    When enabled, NzbDav downloads the next episode of a series into a local cache once you've
                    watched far enough into the current one, so it starts playing instantly. Detecting watch
                    progress requires a Jellyfin webhook, configured separately.
                </Form.Text>
            </Form.Group>
            {isEnabled && <>
                <hr />
                <Form.Group>
                    <Form.Label htmlFor="cache-dir-input">Cache Directory</Form.Label>
                    <Form.Control
                        className={styles.input}
                        type="text"
                        id="cache-dir-input"
                        aria-describedby="cache-dir-help"
                        placeholder="/config/cache"
                        value={config["cache.dir"]}
                        onChange={e => setNewConfig({ ...config, "cache.dir": e.target.value })} />
                    <Form.Text id="cache-dir-help" muted>
                        Where prefetched episodes are stored. Leave empty to use a `cache` subdirectory of your
                        config directory. Should be fast local storage, not a network share.
                    </Form.Text>
                </Form.Group>
                <hr />
                <Form.Group>
                    <Form.Label htmlFor="min-free-space-input">Minimal Free Space</Form.Label>
                    <InputGroup className={styles.input}>
                        <Form.Control
                            className={!isValidMinFreeSpaceGb(config["cache.min-free-space-gb"]) ? styles.error : undefined}
                            type="text"
                            id="min-free-space-input"
                            aria-describedby="min-free-space-help"
                            placeholder="10"
                            value={config["cache.min-free-space-gb"]}
                            onChange={e => setNewConfig({ ...config, "cache.min-free-space-gb": e.target.value })} />
                        <InputGroup.Text>GB</InputGroup.Text>
                    </InputGroup>
                    <Form.Text id="min-free-space-help" muted>
                        NzbDav keeps at least this much space free on the cache disk, evicting cached episodes if
                        needed, so other system functions aren't impacted.
                    </Form.Text>
                </Form.Group>
                <hr />
                <Form.Group>
                    <Form.Label htmlFor="prefetch-threshold-input">Prefetch Threshold</Form.Label>
                    <InputGroup className={styles.input}>
                        <Form.Control
                            className={!isValidPrefetchThreshold(config["cache.prefetch-threshold-percent"]) ? styles.error : undefined}
                            type="text"
                            id="prefetch-threshold-input"
                            aria-describedby="prefetch-threshold-help"
                            placeholder="80"
                            value={config["cache.prefetch-threshold-percent"]}
                            onChange={e => setNewConfig({ ...config, "cache.prefetch-threshold-percent": e.target.value })} />
                        <InputGroup.Text>%</InputGroup.Text>
                    </InputGroup>
                    <Form.Text id="prefetch-threshold-help" muted>
                        How far into the current episode you need to watch before NzbDav starts prefetching the
                        next one.
                    </Form.Text>
                </Form.Group>
                <hr />
                <Form.Group>
                    <Form.Label htmlFor="max-cache-time-input">Maximum Cache Time</Form.Label>
                    <InputGroup className={styles.input}>
                        <Form.Control
                            className={!isValidMaxCacheTime(config["cache.max-cache-time-hours"]) ? styles.error : undefined}
                            type="text"
                            id="max-cache-time-input"
                            aria-describedby="max-cache-time-help"
                            placeholder="48"
                            value={config["cache.max-cache-time-hours"]}
                            onChange={e => setNewConfig({ ...config, "cache.max-cache-time-hours": e.target.value })} />
                        <InputGroup.Text>hours</InputGroup.Text>
                    </InputGroup>
                    <Form.Text id="max-cache-time-help" muted>
                        How long a prefetched episode may stay in the cache before it's evicted, whether or not
                        it's been watched yet.
                    </Form.Text>
                </Form.Group>
                <hr />
                <Form.Group>
                    <Form.Label htmlFor="max-cache-episodes-input">Maximum Cache Episodes</Form.Label>
                    <Form.Control
                        {...className([styles.input, !isValidMaxCacheEpisodes(config["cache.max-cache-episodes"]) && styles.error])}
                        type="text"
                        id="max-cache-episodes-input"
                        aria-describedby="max-cache-episodes-help"
                        placeholder="5"
                        value={config["cache.max-cache-episodes"]}
                        onChange={e => setNewConfig({ ...config, "cache.max-cache-episodes": e.target.value })} />
                    <Form.Text id="max-cache-episodes-help" muted>
                        The maximum number of episodes kept cached at once. The least-recently-accessed episode
                        is evicted first once this limit is reached.
                    </Form.Text>
                </Form.Group>
            </>}
        </div>
    );
}

export function isCacheSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["cache.prefetch-enabled"] !== newConfig["cache.prefetch-enabled"]
        || config["cache.dir"] !== newConfig["cache.dir"]
        || config["cache.min-free-space-gb"] !== newConfig["cache.min-free-space-gb"]
        || config["cache.prefetch-threshold-percent"] !== newConfig["cache.prefetch-threshold-percent"]
        || config["cache.max-cache-time-hours"] !== newConfig["cache.max-cache-time-hours"]
        || config["cache.max-cache-episodes"] !== newConfig["cache.max-cache-episodes"];
}

export function isCacheSettingsValid(newConfig: Record<string, string>) {
    return isValidMinFreeSpaceGb(newConfig["cache.min-free-space-gb"])
        && isValidPrefetchThreshold(newConfig["cache.prefetch-threshold-percent"])
        && isValidMaxCacheTime(newConfig["cache.max-cache-time-hours"])
        && isValidMaxCacheEpisodes(newConfig["cache.max-cache-episodes"]);
}

function isValidMinFreeSpaceGb(value: string): boolean {
    if (value.trim() === "") return false;
    const num = Number(value);
    return Number.isFinite(num) && num >= 0;
}

function isValidPrefetchThreshold(value: string): boolean {
    if (value.trim() === "") return false;
    const num = Number(value);
    return Number.isInteger(num) && num >= 1 && num <= 100;
}

function isValidMaxCacheTime(value: string): boolean {
    if (value.trim() === "") return false;
    const num = Number(value);
    return Number.isFinite(num) && num > 0;
}

function isValidMaxCacheEpisodes(value: string): boolean {
    return isPositiveInteger(value);
}
