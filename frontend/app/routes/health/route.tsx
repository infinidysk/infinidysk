import type { Route } from "./+types/route";
import { backendClient } from "~/clients/backend-client.server";
import { HealthTable } from "./components/health-table/health-table";
import { HealthStats } from "./components/health-stats/health-stats";
import {
  HealthHistoryTable,
  type HealthHistoryFilter,
} from "./components/health-history-table/health-history-table";
import { useCallback, useEffect, useState } from "react";
import { useRevalidator, useSearchParams } from "react-router";
import { useWebsocketTopics } from "~/utils/shared-websocket";
import { Alert, Icon, PageHeader } from "~/components/ui";
import type {
  HealthCheckQueueResponse,
  HealthResult,
  RepairAction,
} from "~/clients/backend-client.server";
import { completeHealthCheck, type HealthQueueState } from "./health-queue-state";
import { withUrlBase } from "~/utils/url-base";

const topicNames = {
  healthItemStatus: "hs",
  healthItemProgress: "hp",
};
const topicSubscriptions = {
  [topicNames.healthItemStatus]: "event",
  [topicNames.healthItemProgress]: "event",
} as const;

const PAGE_SIZE_OPTIONS = [25, 50, 100, 250] as const;
const DEFAULT_PAGE_SIZE = 25;

function parsePage(value: string | null): number {
  const page = parseInt(value ?? "1", 10);
  return Number.isFinite(page) && page > 0 ? page : 1;
}

function parsePageSize(value: string | null): number {
  const size = parseInt(value ?? String(DEFAULT_PAGE_SIZE), 10);
  return (PAGE_SIZE_OPTIONS as readonly number[]).includes(size) ? size : DEFAULT_PAGE_SIZE;
}

function parseHistoryFilter(value: string | null): HealthHistoryFilter {
  return value === "deleted" || value === "repaired" || value === "degraded" ? value : "all";
}

export async function loader({ request }: Route.LoaderArgs) {
  const enabledKey = "repair.enable";
  const url = new URL(request.url);
  const historyPage = parsePage(url.searchParams.get("page"));
  const historyPageSize = parsePageSize(url.searchParams.get("pageSize"));
  const historyFilter = parseHistoryFilter(url.searchParams.get("status"));
  // Degraded is a HealthResult, not a RepairAction, so it filters on `result`
  // instead of `repairStatus`.
  const repairStatus =
    historyFilter === "all"
      ? "deleted,repaired"
      : historyFilter === "degraded"
        ? undefined
        : historyFilter;
  const result = historyFilter === "degraded" ? "degraded" : undefined;
  const [queueData, historyData, config] = await Promise.all([
    backendClient.getHealthCheckQueue(30),
    backendClient.getHealthCheckHistory({
      page: historyPage,
      pageSize: historyPageSize,
      ...(repairStatus !== undefined ? { repairStatus } : {}),
      ...(result !== undefined ? { result } : {}),
    }),
    backendClient.getConfig([enabledKey]),
  ]);

  return {
    uncheckedCount: queueData.uncheckedCount,
    queueItems: queueData.items,
    historyStats: historyData.stats,
    historyItems: historyData.items,
    historyTotalCount: historyData.totalCount,
    historyPage,
    historyPageSize,
    historyFilter,
    isEnabled:
      config
        .filter((x) => x.configName === enabledKey)
        .filter((x) => x.configValue.toLowerCase() === "true").length > 0,
  };
}

export default function Health({ loaderData }: Route.ComponentProps) {
  const { isEnabled } = loaderData;
  const [historyStats, setHistoryStats] = useState(loaderData.historyStats);
  const [historyItems, setHistoryItems] = useState(loaderData.historyItems);
  const [historyTotalCount, setHistoryTotalCount] = useState(loaderData.historyTotalCount);
  const [queueState, setQueueState] = useState<HealthQueueState>({
    items: loaderData.queueItems,
    uncheckedCount: loaderData.uncheckedCount,
  });
  const { items: queueItems, uncheckedCount } = queueState;
  const [, setSearchParams] = useSearchParams();
  const revalidator = useRevalidator();

  useEffect(() => {
    setHistoryStats(loaderData.historyStats);
  }, [loaderData.historyStats]);
  useEffect(() => {
    setHistoryItems(loaderData.historyItems);
  }, [loaderData.historyItems]);
  useEffect(() => {
    setHistoryTotalCount(loaderData.historyTotalCount);
  }, [loaderData.historyTotalCount]);
  useEffect(() => {
    setQueueState({
      items: loaderData.queueItems,
      uncheckedCount: loaderData.uncheckedCount,
    });
  }, [loaderData.queueItems, loaderData.uncheckedCount]);

  const setHistoryParams = useCallback(
    (params: { page?: number; pageSize?: number; status?: HealthHistoryFilter }) => {
      setSearchParams(
        (previous) => {
          const next = new URLSearchParams(previous);
          if (params.page !== undefined) next.set("page", String(params.page));
          if (params.pageSize !== undefined) next.set("pageSize", String(params.pageSize));
          if (params.status !== undefined) {
            if (params.status === "all") next.delete("status");
            else next.set("status", params.status);
          }
          return next;
        },
        { preventScrollReset: true },
      );
    },
    [setSearchParams],
  );

  const onHistoryFilterSelected = useCallback(
    (filter: HealthHistoryFilter) => {
      setHistoryParams({ status: filter, page: 1 });
    },
    [setHistoryParams],
  );

  const onHistoryPageSizeSelected = useCallback(
    (pageSize: number) => {
      setHistoryParams({ pageSize, page: 1 });
    },
    [setHistoryParams],
  );

  // effects
  useEffect(() => {
    if (queueItems.length >= 15) return;
    const refetchData = async () => {
      const response = await fetch(withUrlBase("/api/get-health-check-queue?pageSize=30"));
      if (response.ok) {
        // /api/get-health-check-queue returns HealthCheckQueueResponse
        const healthCheckQueue = (await response.json()) as HealthCheckQueueResponse;
        setQueueState({
          items: healthCheckQueue.items,
          uncheckedCount: healthCheckQueue.uncheckedCount,
        });
      }
    };
    void refetchData(); // fire-and-forget queue refill
  }, [queueItems, setQueueState]);

  // events
  const onHealthItemStatus = useCallback(
    (message: string) => {
      const [davItemId, healthResult, repairAction] = message.split("|");
      setQueueState((x) => completeHealthCheck(x, davItemId!));
      setHistoryStats((x) => {
        // 'hs' websocket payload carries numeric HealthResult / RepairAction enum values
        const healthResultNum: HealthResult = Number(healthResult);
        const repairActionNum: RepairAction = Number(repairAction);

        // attempt to find and update a matching statistic
        let updated = false;
        const newStats = x.map((stat) => {
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
              count: 1,
            },
          ];
        }

        // if an update occurred, return the modified array
        return newStats;
      });
    },
    [setQueueState, setHistoryStats],
  );

  const onHealthItemProgress = useCallback(
    (message: string) => {
      const [davItemId, progress] = message.split("|");
      if (progress === "done") return;
      setQueueState((queueState) => {
        const index = queueState.items.findIndex((x) => x.id === davItemId);
        if (index === -1) return queueState;
        return {
          ...queueState,
          items: queueState.items
            .filter((_, i) => i >= index)
            .map((item) =>
              item.id === davItemId ? { ...item, progress: Number(progress) } : item,
            ),
        };
      });
    },
    [setQueueState],
  );

  // websocket
  const onWebsocketMessage = useCallback(
    (topic: string, message: string) => {
      if (topic == topicNames.healthItemStatus) onHealthItemStatus(message);
      else if (topic == topicNames.healthItemProgress) onHealthItemProgress(message);
    },
    [onHealthItemStatus, onHealthItemProgress],
  );

  useWebsocketTopics(topicSubscriptions, onWebsocketMessage);

  return (
    <div className="flex min-h-full min-w-full flex-col gap-8 px-4 py-4 text-sm text-base-content md:px-8">
      <PageHeader
        title="Health"
        subtitle="Repair queue and history for files that fail Usenet article checks."
        actions={
          <a className="btn btn-sm" href="#repair-history">
            <Icon name="history" className="!text-[16px]" />
            Jump to history
          </a>
        }
      />
      <HealthStats stats={historyStats} />
      {isEnabled && uncheckedCount > 20 && (
        <Alert className="alert-soft" variant="warning">
          <Icon name="warning" filled className="shrink-0 !text-[20px]" />
          <div>
            <div className="font-semibold">Initial health scan pending</div>
            <p className="mt-1 text-xs leading-relaxed text-base-content/70">
              About {uncheckedCount} files have never been health-checked. The queue will run an
              initial scan; later checks are much less frequent.
            </p>
          </div>
        </Alert>
      )}
      <HealthTable
        isEnabled={isEnabled}
        healthCheckItems={queueItems.filter((_, index) => index < 10)}
      />
      <HealthHistoryTable
        items={historyItems}
        totalCount={historyTotalCount}
        page={loaderData.historyPage}
        pageSize={loaderData.historyPageSize}
        pageSizeOptions={PAGE_SIZE_OPTIONS}
        filter={loaderData.historyFilter}
        refreshing={revalidator.state !== "idle"}
        onFilterSelected={onHistoryFilterSelected}
        onPageSelected={(page) => setHistoryParams({ page })}
        onPageSizeSelected={onHistoryPageSizeSelected}
        onRefresh={() => void revalidator.revalidate()}
      />
    </div>
  );
}
