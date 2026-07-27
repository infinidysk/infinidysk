import { Link } from "react-router";
import { ConfirmModal } from "~/components/confirm-modal/confirm-modal";
import { settingsPath } from "~/routes/settings/settings-tabs";

export type DisableTracingConfirmModalProps = {
    show: boolean;
    eventCount: number;
    sessionCount: number;
    includeSettingsLink?: boolean;
    onCancel: () => void;
    onConfirm: () => void;
};

export function DisableTracingConfirmModal({
    show,
    eventCount,
    sessionCount,
    includeSettingsLink = false,
    onCancel,
    onConfirm,
}: DisableTracingConfirmModalProps) {
    const counts = `${eventCount.toLocaleString()} events across ${sessionCount.toLocaleString()} sessions`;

    return (
        <ConfirmModal
            show={show}
            title="Turn off stream tracing?"
            message={
                <>
                    Tracing is holding {counts} in memory. Turning it off releases the buffer
                    immediately, and those traces cannot be recovered. Generate a support pack
                    first if you want them included.
                    {includeSettingsLink && (
                        <>
                            {" "}
                            <Link
                                to={settingsPath("support")}
                                className="link link-primary"
                                onClick={onCancel}
                            >
                                Open Support settings
                            </Link>
                            {" "}to generate a pack.
                        </>
                    )}
                </>
            }
            cancelText="Keep tracing on"
            confirmText="Turn off and discard"
            onCancel={onCancel}
            onConfirm={onConfirm}
        />
    );
}
