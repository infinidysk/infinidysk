import { Form, InputGroup } from "react-bootstrap";
import styles from "./webdav.module.css"
import { type Dispatch, type SetStateAction, useEffect, useState } from "react";
import { className } from "~/utils/styling";
import { isPositiveInteger } from "../usenet/usenet";
import { receiveMessage } from "~/utils/websocket-util";

type SabnzbdSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

export function WebdavSettings({ config, setNewConfig }: SabnzbdSettingsProps) {
    const bandwidthUsage = useBandwidthUsage();
    return (
        <div className={styles.container}>
            <Form.Group>
                <Form.Label htmlFor="webdav-user-input">WebDAV User</Form.Label>
                <Form.Control
                    {...className([styles.input, !isValidUser(config["webdav.user"]) && styles.error])}
                    type="text"
                    id="webdav-user-input"
                    aria-describedby="webdav-user-help"
                    placeholder="admin"
                    value={config["webdav.user"]}
                    onChange={e => setNewConfig({ ...config, "webdav.user": e.target.value })} />
                <Form.Text id="webdav-user-help" muted>
                    Use this user to connect to the webdav. Only letters, numbers, dashes, and underscores allowed.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="webdav-pass-input">WebDAV Password</Form.Label>
                <Form.Control
                    className={styles.input}
                    type="password"
                    id="webdav-pass-input"
                    aria-describedby="webdav-pass-help"
                    value={config["webdav.pass"]}
                    onChange={e => setNewConfig({ ...config, "webdav.pass": e.target.value })} />
                <Form.Text id="webdav-pass-help" muted>
                    Use this password to connect to the webdav.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="max-download-connections-input">Max Download Connections</Form.Label>
                <Form.Control
                    {...className([styles.input, !isValidMaxDownloadConnections(config["usenet.max-download-connections"]) && styles.error])}
                    type="text"
                    id="max-download-connections-input"
                    aria-describedby="max-download-connections-help"
                    placeholder="15"
                    value={config["usenet.max-download-connections"]}
                    onChange={e => setNewConfig({ ...config, "usenet.max-download-connections": e.target.value })} />
                <Form.Text id="max-download-connections-help" muted>
                    The maximum number of connections that will be used for downloading articles from your usenet provider(s).
                    Configure this to the minimum number of connections that will fully saturate your server's bandwidth.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="streaming-priority-input">Streaming Priority (vs Queue)</Form.Label>
                <InputGroup className={styles.input}>
                    <Form.Control
                        className={!isValidStreamingPriority(config["usenet.streaming-priority"]) ? styles.error : undefined}
                        type="text"
                        id="streaming-priority-input"
                        aria-describedby="streaming-priority-help"
                        placeholder="80"
                        value={config["usenet.streaming-priority"]}
                        onChange={e => setNewConfig({ ...config, "usenet.streaming-priority": e.target.value })} />
                    <InputGroup.Text>%</InputGroup.Text>
                </InputGroup>
                <Form.Text id="streaming-priority-help" muted>
                    When streaming from the webdav while the queue is also active, how much bandwidth should be dedicated to streaming?
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="article-buffer-size-input">Article Buffer Size</Form.Label>
                <Form.Control
                    {...className([styles.input, !isValidArticleBufferSize(config["usenet.article-buffer-size"]) && styles.error])}
                    type="text"
                    id="article-buffer-size-input"
                    aria-describedby="article-buffer-size-help"
                    placeholder="40"
                    value={config["usenet.article-buffer-size"]}
                    onChange={e => setNewConfig({ ...config, "usenet.article-buffer-size": e.target.value })} />
                <Form.Text id="article-buffer-size-help" muted>
                    The number of articles to buffer ahead, per stream, when reading from the webdav.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Label htmlFor="bandwidth-limit-input">Bandwidth Limit</Form.Label>
                <InputGroup className={styles.input}>
                    <Form.Control
                        className={!isValidBandwidthLimit(config["usenet.bandwidth-limit-mbps"]) ? styles.error : undefined}
                        type="text"
                        id="bandwidth-limit-input"
                        aria-describedby="bandwidth-limit-help"
                        placeholder="unlimited"
                        value={config["usenet.bandwidth-limit-mbps"]}
                        onChange={e => setNewConfig({ ...config, "usenet.bandwidth-limit-mbps": e.target.value })} />
                    <InputGroup.Text>Mbit/s</InputGroup.Text>
                </InputGroup>
                <Form.Text id="bandwidth-limit-help" muted>
                    The maximum combined rate NzbDav will use to download from your usenet provider(s), across
                    both queue imports and webdav streaming. Leave empty for no limit. Note: 1 MB/s = 8 Mbit/s.
                    Not sure what your connection can handle? Try a{" "}
                    <a href="https://www.speedtest.net/" target="_blank" rel="noreferrer">speed test</a>.
                </Form.Text>
                {isLowBandwidthLimit(config["usenet.bandwidth-limit-mbps"]) &&
                    <Form.Text className="d-block text-warning">
                        Very low limits can make downloads feel uneven, since NzbDav has to pause frequently to stay under the cap.
                    </Form.Text>}
                {config["usenet.bandwidth-limit-mbps"].trim() !== "" && bandwidthUsage &&
                    <Form.Text className="d-block">
                        Current usage: {bandwidthUsage.currentMbps.toFixed(1)} / {bandwidthUsage.limitMbps.toFixed(1)} Mbit/s
                    </Form.Text>}
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="checkbox"
                    id="readonly-checkbox"
                    aria-describedby="readonly-help"
                    label={`Enforce Read-Only`}
                    checked={config["webdav.enforce-readonly"] === "true"}
                    onChange={e => setNewConfig({ ...config, "webdav.enforce-readonly": "" + e.target.checked })} />
                <Form.Text id="readonly-help" muted>
                    The WebDAV `/content` folder will be readonly when checked. WebDAV clients will not be able to delete files within this directory.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="checkbox"
                    id="show-hidden-files-checkbox"
                    aria-describedby="show-hidden-files-help"
                    label={`Show hidden files on Dav Explorer`}
                    checked={config["webdav.show-hidden-files"] === "true"}
                    onChange={e => setNewConfig({ ...config, "webdav.show-hidden-files": "" + e.target.checked })} />
                <Form.Text id="show-hidden-files-help" muted>
                    Hidden files or directories are those whose names are prefixed by a period.
                </Form.Text>
            </Form.Group>
            <hr />
            <Form.Group>
                <Form.Check
                    className={styles.input}
                    type="checkbox"
                    id="preview-par2-files-checkbox"
                    aria-describedby="preview-par2-files-help"
                    label={`Preview par2 files on Dav Explorer`}
                    checked={config["webdav.preview-par2-files"] === "true"}
                    onChange={e => setNewConfig({ ...config, "webdav.preview-par2-files": "" + e.target.checked })} />
                <Form.Text id="preview-par2-files-help" muted>
                    When enabled, par2 files will be rendered as text files on the Dav Explorer page, displaying all File-Descriptor entries.
                </Form.Text>
            </Form.Group>
        </div>
    );
}

export function isWebdavSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["webdav.user"] !== newConfig["webdav.user"]
        || config["webdav.pass"] !== newConfig["webdav.pass"]
        || config["usenet.max-download-connections"] !== newConfig["usenet.max-download-connections"]
        || config["usenet.streaming-priority"] !== newConfig["usenet.streaming-priority"]
        || config["usenet.article-buffer-size"] !== newConfig["usenet.article-buffer-size"]
        || config["usenet.bandwidth-limit-mbps"] !== newConfig["usenet.bandwidth-limit-mbps"]
        || config["webdav.show-hidden-files"] !== newConfig["webdav.show-hidden-files"]
        || config["webdav.enforce-readonly"] !== newConfig["webdav.enforce-readonly"]
        || config["webdav.preview-par2-files"] !== newConfig["webdav.preview-par2-files"]
}

export function isWebdavSettingsValid(newConfig: Record<string, string>) {
    return isValidUser(newConfig["webdav.user"])
        && isValidMaxDownloadConnections(newConfig["usenet.max-download-connections"])
        && isValidStreamingPriority(newConfig["usenet.streaming-priority"])
        && isValidArticleBufferSize(newConfig["usenet.article-buffer-size"])
        && isValidBandwidthLimit(newConfig["usenet.bandwidth-limit-mbps"]);
}

function isValidUser(user: string): boolean {
    const regex = /^[A-Za-z0-9_-]+$/;
    return regex.test(user);
}

function isValidMaxDownloadConnections(value: string): boolean {
    return isPositiveInteger(value);
}

function isValidStreamingPriority(value: string): boolean {
    if (value.trim() === "") return false;
    const num = Number(value);
    return Number.isInteger(num) && num >= 0 && num <= 100;
}

function isValidArticleBufferSize(value: string): boolean {
    return isPositiveInteger(value);
}

function isValidBandwidthLimit(value: string): boolean {
    if (value.trim() === "") return true;
    const num = Number(value);
    return Number.isFinite(num) && num > 0;
}

function isLowBandwidthLimit(value: string): boolean {
    if (value.trim() === "") return false;
    const num = Number(value);
    return Number.isFinite(num) && num > 0 && num < 2;
}

type BandwidthUsage = { currentMbps: number, limitMbps: number };

const bandwidthUsageTopic = { "bwu": "state" };

function useBandwidthUsage(): BandwidthUsage | null {
    const [usage, setUsage] = useState<BandwidthUsage | null>(null);

    useEffect(() => {
        let ws: WebSocket;
        let disposed = false;

        function connect() {
            ws = new WebSocket(window.location.origin.replace(/^http/, "ws"));
            ws.onmessage = receiveMessage((topic, message) => {
                if (topic !== "bwu") return;
                if (message === "off" || message === "") {
                    setUsage(null);
                    return;
                }
                const [current, limit] = message.split("|").map(Number);
                setUsage({ currentMbps: current, limitMbps: limit });
            });
            ws.onopen = () => ws.send(JSON.stringify(bandwidthUsageTopic));
            ws.onerror = () => ws.close();
            ws.onclose = () => { if (!disposed) setTimeout(connect, 1000); };
        }

        connect();
        return () => { disposed = true; ws?.close(); };
    }, []);

    return usage;
}