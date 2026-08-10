import type { Route } from "./+types/route";
import { backendClient } from "~/clients/backend-client.server";
import { HealthTable } from "./components/health-table/health-table";
import { HealthStats } from "./components/health-stats/health-stats";
import { useCallback, useEffect, useState } from "react";
import { useWebsocketTopics } from "~/utils/shared-websocket";
import { Alert, Icon } from "~/components/ui";
import type { HealthCheckQueueResponse, HealthResult, RepairAction } from "~/clients/backend-client.server";
import { completeHealthCheck, type HealthQueueState } from "./health-queue-state";
import { withUrlBase } from "~/utils/url-base";

const topicNames = {
    healthItemStatus: 'hs',
    healthItemProgress: 'hp',
}
const topicSubscriptions = {
    [topicNames.healthItemStatus]: 'event',
    [topicNames.healthItemProgress]: 'event',
} as const;

export async function loader() {
    const enabledKey = 'repair.enable';
    const [queueData, historyData, config] = await Promise.all([
        backendClient.getHealthCheckQueue(30),
        backendClient.getHealthCheckHistory(),
        backendClient.getConfig([enabledKey])
    ]);

    return {
        uncheckedCount: queueData.uncheckedCount,
        queueItems: queueData.items,
        historyStats: historyData.stats,
        historyItems: historyData.items,
        isEnabled: config
            .filter(x => x.configName === enabledKey)
            .filter(x => x.configValue.toLowerCase() === "true")
            .length > 0
    };
}

export default function Health({ loaderData }: Route.ComponentProps) {
    const { isEnabled } = loaderData;
    const [historyStats, setHistoryStats] = useState(loaderData.historyStats);
    const [queueState, setQueueState] = useState<HealthQueueState>({
        items: loaderData.queueItems,
        uncheckedCount: loaderData.uncheckedCount,
    });
    const { items: queueItems, uncheckedCount } = queueState;

    // effects
    useEffect(() => {
        if (queueItems.length >= 15) return;
        const refetchData = async () => {
            const response = await fetch(withUrlBase('/api/get-health-check-queue?pageSize=30'));
            if (response.ok) {
                // /api/get-health-check-queue returns HealthCheckQueueResponse
                const healthCheckQueue = await response.json() as HealthCheckQueueResponse;
                setQueueState({
                    items: healthCheckQueue.items,
                    uncheckedCount: healthCheckQueue.uncheckedCount,
                });
            }
        };
        void refetchData(); // fire-and-forget queue refill
    }, [queueItems, setQueueState]);

    // events
    const onHealthItemStatus = useCallback((message: string) => {
        const [davItemId, healthResult, repairAction] = message.split('|');
        setQueueState(x => completeHealthCheck(x, davItemId!));
        setHistoryStats(x => {
            // 'hs' websocket payload carries numeric HealthResult / RepairAction enum values
            const healthResultNum: HealthResult = Number(healthResult);
            const repairActionNum: RepairAction = Number(repairAction);

            // attempt to find and update a matching statistic
            let updated = false;
            const newStats = x.map(stat => {
                if (stat.result === healthResultNum && stat.repairStatus === repairActionNum) {
                    updated = true;
                    return { ...stat, count: stat.count + 1 };
                }
                return stat;
            });

            // if no statistic was updated, add a new one
            if (!updated) {
                return [
                    ...x,
                    {
                        result: healthResultNum,
                        repairStatus: repairActionNum,
                        count: 1
                    }
                ];
            }

            // if an update occurred, return the modified array
            return newStats;
        });
    }, [setQueueState, setHistoryStats]);

    const onHealthItemProgress = useCallback((message: string) => {
        const [davItemId, progress] = message.split('|');
        if (progress === "done") return;
        setQueueState(queueState => {
            const index = queueState.items.findIndex(x => x.id === davItemId);
            if (index === -1) return queueState;
            return {
                ...queueState,
                items: queueState.items
                    .filter((_, i) => i >= index)
                    .map(item => item.id === davItemId
                        ? { ...item, progress: Number(progress) }
                        : item
                    ),
            };
        });
    }, [setQueueState]);

    // websocket
    const onWebsocketMessage = useCallback((topic: string, message: string) => {
        if (topic == topicNames.healthItemStatus)
            onHealthItemStatus(message);
        else if (topic == topicNames.healthItemProgress)
            onHealthItemProgress(message);
    }, [
        onHealthItemStatus,
        onHealthItemProgress
    ]);

    useWebsocketTopics(topicSubscriptions, onWebsocketMessage);

    return (
        <div className="flex min-h-full min-w-full flex-col gap-8 px-4 py-4 text-sm text-base-content md:px-8">
            <HealthStats stats={historyStats} />
            {isEnabled && uncheckedCount > 20 &&
                <Alert className="alert-soft" variant="warning">
                    <Icon name="warning" filled className="shrink-0 !text-[20px]" />
                    <div>
                        <div className="font-semibold">Initial health scan pending</div>
                        <p className="mt-1 text-xs leading-relaxed text-base-content/70">
                            About {uncheckedCount} files have never been health-checked. The queue will
                            run an initial scan; later checks are much less frequent.
                        </p>
                    </div>
                </Alert>
            }
            <HealthTable isEnabled={isEnabled} healthCheckItems={queueItems.filter((_, index) => index < 10)} />
        </div>
    );
}