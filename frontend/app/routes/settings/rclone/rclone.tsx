import { Button } from "~/components/ui/button";
import { Alert, Spinner, Tooltip } from "~/components/ui/feedback";
import { ManagedSetting, SettingsCard, SettingsIntro, SettingsPage } from "~/components/ui";
import { Input, Toggle } from "~/components/ui/form";
import { Icon } from "~/components/ui/icon";
import { type Dispatch, type SetStateAction, useState, useCallback, useEffect } from "react";
import { withUrlBase } from "~/utils/url-base";

type RcloneSettingsProps = {
    config: Record<string, string>
    setNewConfig: Dispatch<SetStateAction<Record<string, string>>>
};

export function RcloneSettings({ config, setNewConfig }: RcloneSettingsProps) {
    const [connectionState, setConnectionState] = useState<'idle' | 'testing' | 'success' | 'error'>('idle');
    const [testError, setTestError] = useState<string | null>(null);
    const [invalidationError, setInvalidationError] = useState<string | null>(null);

    useEffect(() => {
        setConnectionState('idle');
        setTestError(null);
        setInvalidationError(null);
    }, [config["rclone.host"], config["rclone.user"], config["rclone.pass"]]);

    const testConnection = useCallback(async () => {
        const host = config["rclone.host"];
        if (!host?.trim()) {
            return;
        }

        setConnectionState('testing');
        setTestError(null);
        setInvalidationError(null);

        try {
            const formData = new FormData();
            formData.append('host', host);
            formData.append('user', config["rclone.user"] ?? '');
            formData.append('pass', config["rclone.pass"] ?? '');

            const response = await fetch(withUrlBase('/api/test-rclone-connection'), {
                method: 'POST',
                body: formData
            });

            const result = await response.json();

            if (result.status && result.connected) {
                setConnectionState('success');
                setTestError(null);
                setInvalidationError(result.lastInvalidationError ?? null);
            } else {
                setConnectionState('error');
                setTestError(result.error || "Connection test failed");
            }
        } catch (error) {
            setConnectionState('error');
            setTestError(error instanceof Error ? error.message : "Connection test failed");
        }
    }, [config]);

    return (
        <SettingsPage>
            <SettingsIntro>
                Connect InfiniDysk to an rclone Remote Control server so mounted directory caches can be
                refreshed automatically when files change.
            </SettingsIntro>

            <div className="flex flex-col gap-4">
            <SettingsCard
                icon="notifications_active"
                title="RC notifications"
                description="Notify the rclone mount whenever WebDAV content is added or removed."
            >
            <ManagedSetting configKey="rclone.rc-enabled">
            <Tooltip placement="bottom" content="Notify your rclone mount via the RC API when files are added or removed on the WebDAV, so you can use a high dir-cache-time.">
                <Toggle
                    id="rclone-rc-enabled-checkbox"
                    className="cursor-pointer gap-2 p-0"
                    checked={config["rclone.rc-enabled"] === "true"}
                    onChange={e => setNewConfig({ ...config, "rclone.rc-enabled": "" + e.target.checked })}
                    label={<span className="text-sm text-base-content">Enable Rclone RC Server Notifications</span>}
                />
            </Tooltip>
            </ManagedSetting>
            </SettingsCard>

            <SettingsCard
                icon="dns"
                title="Server connection"
                description="Configure and test access to the rclone Remote Control API."
                contentClassName="grid grid-cols-1 gap-4 lg:grid-cols-2"
            >
            <ManagedSetting configKey="rclone.host" className="lg:col-span-2">
            <div className="space-y-2">
                <label className="block text-sm font-medium text-base-content" htmlFor="rclone-host-input">Rclone Server Host</label>
                <div className="flex w-full">
                    <Input
                        type="text"
                        id="rclone-host-input"
                        aria-describedby="rclone-host-help"
                        placeholder="http://localhost:5572"
                        value={config["rclone.host"]}
                        onChange={e => setNewConfig({ ...config, "rclone.host": e.target.value })} />
                    {config["rclone.host"]?.trim() && (
                        <Tooltip content="Tests host, credentials, and API response">
                            <Button
                                variant={connectionState === 'success' ? 'success' :
                                    connectionState === 'error' ? 'danger' : 'secondary'}
                                onClick={testConnection}
                                disabled={connectionState === 'testing'}
                                className={'shrink-0'}
                            >
                                {
                                    connectionState === 'testing' ? (
                                        <Spinner />
                                    ) : connectionState === 'success' ? (
                                        <Icon name="check" className="!text-[18px]" />
                                    ) : connectionState === 'error' ? (
                                        <Icon name="close" className="!text-[18px]" />
                                    ) : (
                                        'Test Conn'
                                    )
                                }
                            </Button>
                        </Tooltip>
                    )}
                </div>
                {connectionState === 'error' && testError && (
                    <Alert variant="danger" className="text-xs py-2">
                        {testError}
                    </Alert>
                )}
                {connectionState === 'success' && (
                    <Alert variant="success" className="text-xs py-2">
                        Connection test successful
                    </Alert>
                )}
                {connectionState === 'success' && invalidationError && (
                    <Alert variant="warning" className="text-xs py-2">
                        Recent VFS cache invalidation failed: {invalidationError}. Mounted clients may show stale
                        entries until rclone&apos;s dir-cache expires.
                    </Alert>
                )}
                <p className="text-[11px] leading-relaxed text-base-content/45" id="rclone-host-help">
                    The host address of the rclone RC API.
                </p>
            </div>
            </ManagedSetting>
            <ManagedSetting configKey="rclone.user">
            <div className="space-y-2">
                <label className="block text-sm font-medium text-base-content" htmlFor="rclone-user-input">Rclone Server User</label>
                <Input
                    className={'w-full'}
                    type="text"
                    id="rclone-user-input"
                    aria-describedby="rclone-user-help"
                    value={config["rclone.user"]}
                    onChange={e => setNewConfig({ ...config, "rclone.user": e.target.value })} />
                <p className="text-[11px] leading-relaxed text-base-content/45" id="rclone-user-help">
                    The username for authenticating to the rclone RC API. This field is optional.
                </p>
            </div>
            </ManagedSetting>
            <ManagedSetting configKey="rclone.pass">
            <div className="space-y-2">
                <label className="block text-sm font-medium text-base-content" htmlFor="rclone-pass-input">Rclone Server Password</label>
                <Input
                    className={'w-full'}
                    type="password"
                    id="rclone-pass-input"
                    aria-describedby="rclone-pass-help"
                    value={config["rclone.pass"]}
                    onChange={e => setNewConfig({ ...config, "rclone.pass": e.target.value })} />
                <p className="text-[11px] leading-relaxed text-base-content/45" id="rclone-pass-help">
                    The password for authenticating to the rclone RC API. This field is optional.
                </p>
            </div>
            </ManagedSetting>
            </SettingsCard>
            </div>
        </SettingsPage>
    );
}

export function isRcloneSettingsUpdated(config: Record<string, string>, newConfig: Record<string, string>) {
    return config["rclone.rc-enabled"] !== newConfig["rclone.rc-enabled"]
        || config["rclone.host"] !== newConfig["rclone.host"]
        || config["rclone.user"] !== newConfig["rclone.user"]
        || config["rclone.pass"] !== newConfig["rclone.pass"];
}
