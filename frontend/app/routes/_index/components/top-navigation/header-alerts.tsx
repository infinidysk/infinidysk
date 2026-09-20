import { useEffect, useRef, useState } from "react";
import type {
  ArrHealthResponse,
  LiveStatsMessage,
  ProviderCircuitBreakerRow,
} from "~/clients/backend-client.server";
import { AttentionSummary } from "~/components/attention-summary/attention-summary";
import { useWebsocketTopic } from "~/utils/shared-websocket";
import { withUrlBase } from "~/utils/url-base";
import type { UpdateAvailable } from "~/utils/update-check";

export function HeaderAlerts({
  hasUsenetProviders,
  updateAvailable,
}: {
  hasUsenetProviders: boolean;
  updateAvailable?: UpdateAvailable | null | undefined;
}) {
  const [providers, setProviders] = useState<ProviderCircuitBreakerRow[] | null>(null);
  const [providersLoading, setProvidersLoading] = useState(true);
  const providerTimeout = useRef<ReturnType<typeof setTimeout> | null>(null);
  const [arrHealth, setArrHealth] = useState<ArrHealthResponse | null>(null);
  const [arrLoading, setArrLoading] = useState(true);

  const markProvidersUnavailable = () => {
    setProviders(null);
    setProvidersLoading(false);
  };

  useEffect(() => {
    if (!hasUsenetProviders) return;
    providerTimeout.current = setTimeout(markProvidersUnavailable, 15_000);
    return () => {
      if (providerTimeout.current) clearTimeout(providerTimeout.current);
    };
  }, [hasUsenetProviders]);

  useWebsocketTopic(
    "ls",
    "state",
    (message) => {
      try {
        const data = JSON.parse(message) as LiveStatsMessage;
        if (!Array.isArray(data.providerBreakers)) return;
        if (providerTimeout.current) clearTimeout(providerTimeout.current);
        if (!Number.isFinite(data.ts) || Date.now() - data.ts > 15_000) {
          markProvidersUnavailable();
          return;
        }
        setProviders(data.providerBreakers);
        setProvidersLoading(false);
        providerTimeout.current = setTimeout(markProvidersUnavailable, 15_000);
      } catch {
        markProvidersUnavailable();
      }
    },
    { enabled: hasUsenetProviders, onClose: markProvidersUnavailable },
  );

  useEffect(() => {
    const controller = new AbortController();
    let pending = false;
    const refresh = async () => {
      if (pending || document.hidden) return;
      pending = true;
      const requestController = new AbortController();
      const abortRequest = () => requestController.abort();
      const timeout = setTimeout(abortRequest, 5_000);
      controller.signal.addEventListener("abort", abortRequest, { once: true });
      try {
        const response = await fetch(withUrlBase("/api/get-arr-health?window=1h"), {
          signal: requestController.signal,
        });
        if (!response.ok) throw new Error("Arr status unavailable");
        const data = (await response.json()) as ArrHealthResponse;
        if (typeof data.configured !== "boolean" || !Array.isArray(data.instances))
          throw new Error("Invalid Arr status");
        if (!controller.signal.aborted) setArrHealth(data);
      } catch {
        if (!controller.signal.aborted) setArrHealth(null);
      } finally {
        clearTimeout(timeout);
        controller.signal.removeEventListener("abort", abortRequest);
        pending = false;
        if (!controller.signal.aborted) setArrLoading(false);
      }
    };
    void refresh();
    const interval = setInterval(() => void refresh(), 60_000);
    const onVisible = () => {
      if (!document.hidden) void refresh();
    };
    document.addEventListener("visibilitychange", onVisible);
    return () => {
      controller.abort();
      clearInterval(interval);
      document.removeEventListener("visibilitychange", onVisible);
    };
  }, []);

  return (
    <AttentionSummary
      updateAvailable={updateAvailable}
      providers={hasUsenetProviders ? providers : []}
      providersLoading={hasUsenetProviders && providersLoading}
      arrHealth={arrHealth}
      arrLoading={arrLoading}
      hasConfiguredArrs={arrHealth?.configured ?? true}
    />
  );
}
