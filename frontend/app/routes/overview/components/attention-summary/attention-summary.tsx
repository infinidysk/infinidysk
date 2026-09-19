import { useEffect, useState } from "react";
import { Link } from "react-router";
import type { ArrHealthResponse, ProviderRow } from "~/clients/backend-client.server";
import { adminApi } from "~/clients/admin-operations";
import { Icon } from "~/components/ui";
import { settingsPath } from "~/navigation/settings-tabs";
import { withUrlBase } from "~/utils/url-base";

const HEALTH_CHECK_TIMEOUT_MS = 5_000;

export function AttentionSummary({
  providers,
  arrHealth,
  hasConfiguredArrs,
}: {
  providers: ProviderRow[] | null;
  arrHealth: ArrHealthResponse | null;
  hasConfiguredArrs: boolean;
}) {
  const [healthCount, setHealthCount] = useState<number | null>(null);
  const [healthError, setHealthError] = useState(false);

  useEffect(() => {
    const controller = new AbortController();
    let pending = false;
    const refresh = async () => {
      if (pending || document.hidden) return;
      pending = true;
      const requestController = new AbortController();
      const abortRequest = () => requestController.abort();
      const timeout = setTimeout(abortRequest, HEALTH_CHECK_TIMEOUT_MS);
      controller.signal.addEventListener("abort", abortRequest, { once: true });
      try {
        const response = await fetch(
          withUrlBase(
            `${adminApi.getHealthCheckHistory}?page=1&pageSize=1&currentActionNeeded=true`,
          ),
          { signal: requestController.signal },
        );
        if (!response.ok) throw new Error("Health status unavailable");
        const data = (await response.json()) as { totalCount?: number };
        if (
          typeof data.totalCount !== "number" ||
          !Number.isFinite(data.totalCount) ||
          data.totalCount < 0
        )
          throw new Error("Invalid health count");
        if (!controller.signal.aborted) {
          setHealthCount(data.totalCount);
          setHealthError(false);
        }
      } catch {
        if (!controller.signal.aborted) {
          setHealthCount(null);
          setHealthError(true);
        }
      } finally {
        clearTimeout(timeout);
        controller.signal.removeEventListener("abort", abortRequest);
        pending = false;
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

  const affectedProviders = providers?.filter(
    (provider) => provider.circuitState === "open" || provider.circuitState === "halfOpen",
  );
  const affectedArrs = arrHealth?.instances.filter(
    (instance) => instance.status === "degraded" || instance.status === "offline",
  );
  const pendingArrs = arrHealth?.instances.some((instance) => instance.status === "pending");

  return (
    <section aria-labelledby="attention-heading" className="border-y border-base-content/15 py-3">
      <h2 id="attention-heading" className="mb-2 text-sm font-semibold text-base-content">
        Needs attention
      </h2>
      <div className="grid items-start gap-2 sm:grid-cols-2 xl:grid-cols-3">
        <AttentionLink
          to="/health"
          icon="health_and_safety"
          warning={healthCount != null && healthCount > 0}
        >
          {healthCount == null
            ? healthError
              ? "Health status unavailable"
              : "Checking health status..."
            : healthCount > 0
              ? `${healthCount.toLocaleString()} files need attention`
              : "No files need attention"}
        </AttentionLink>
        <AttentionLink
          to={settingsPath("usenet")}
          icon="cloud"
          warning={!!affectedProviders?.length}
        >
          {affectedProviders == null
            ? "Provider status unavailable"
            : affectedProviders.length > 0
              ? `${affectedProviders.length} provider circuits open or recovering`
              : "No provider circuits open"}
        </AttentionLink>
        {hasConfiguredArrs && !!affectedArrs?.length ? (
          <details className="group min-w-0 self-start">
            <summary className="flex min-h-11 cursor-pointer list-none items-center gap-2 rounded-sm px-2 py-2 text-sm text-warning hover:bg-base-content/5 focus-visible:outline focus-visible:outline-2 focus-visible:outline-primary">
              <Icon name="sync_alt" className="shrink-0 !text-[18px]" />
              <span className="min-w-0 flex-1">
                {affectedArrs.length} Arr{" "}
                {affectedArrs.length === 1 ? "integration" : "integrations"} degraded or offline
              </span>
              <Icon name="expand_more" className="shrink-0 !text-[18px] group-open:rotate-180" />
            </summary>
            <ul className="space-y-3 px-2 pb-2 text-sm">
              {affectedArrs.map((instance) => (
                <li key={instance.key} className="min-w-0 [overflow-wrap:anywhere]">
                  <p className="font-semibold text-base-content">
                    <span className="capitalize">{instance.appType}</span>: {instance.name}
                  </p>
                  <p className="text-base-content/80">
                    {instance.status === "offline"
                      ? "InfiniDysk could not poll this instance. Check its availability and connection settings."
                      : instance.hasWarnings && instance.hasErrors
                        ? "This app reports queue warnings and errors. Check Activity > Queue in the Arr app."
                        : instance.hasErrors
                          ? "This app reports queue errors. Check Activity > Queue in the Arr app."
                          : instance.hasWarnings
                            ? "This app reports queue warnings. Check Activity > Queue in the Arr app."
                            : "Imports are taking longer than expected. Check Activity > Queue in the Arr app."}
                  </p>
                  {instance.status === "offline" && instance.lastError && (
                    <p className="mt-1 whitespace-pre-wrap text-base-content/80">
                      {instance.lastError}
                    </p>
                  )}
                </li>
              ))}
            </ul>
            <Link to={settingsPath("arrs")} className="link mx-2 text-sm text-base-content/80">
              Arr connection settings
            </Link>
          </details>
        ) : (
          hasConfiguredArrs && (
            <AttentionLink to={settingsPath("arrs")} icon="sync_alt" warning={false}>
              {affectedArrs == null
                ? "Arr status unavailable"
                : pendingArrs
                  ? "Arr status pending"
                  : "No degraded Arr integrations"}
            </AttentionLink>
          )
        )}
      </div>
    </section>
  );
}

function AttentionLink({
  to,
  icon,
  warning,
  children,
}: {
  to: string;
  icon: string;
  warning: boolean;
  children: React.ReactNode;
}) {
  return (
    <Link
      to={to}
      className={`flex min-h-11 min-w-0 items-center gap-2 rounded-sm px-2 py-2 text-sm hover:bg-base-content/5 focus-visible:outline focus-visible:outline-2 focus-visible:outline-primary ${warning ? "text-warning" : "text-base-content/80"}`}
    >
      <Icon name={icon} className="shrink-0 !text-[18px]" />
      <span className="min-w-0 flex-1">{children}</span>
      <Icon name="chevron_right" className="shrink-0 !text-[18px]" />
    </Link>
  );
}
