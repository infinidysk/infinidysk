import type { ArrHealthResponse } from "~/clients/backend-client.server";

const now = () => Date.now();

/** Local-preview payload for `?mockArrHealth` — not served in production. */
export function mockArrHealthData(): ArrHealthResponse {
  const t = now();
  const radarr = "radarr|http://127.0.0.1:7878";
  const sonarr = "sonarr|http://127.0.0.1:8989";
  const sonarr4k = "sonarr|http://127.0.0.1:8990";
  const radarrUhd = "radarr|http://127.0.0.1:7879";

  return {
    configured: true,
    summary: {
      instancesOnline: 3,
      instancesTotal: 4,
      importsCompleted: 42,
      medianHandoffMs: 40_000,
      p95HandoffMs: 71_000,
      awaitingImport: 3,
      awaitingShown: 3,
      degraded: 1,
    },
    instances: [
      {
        key: radarr,
        name: "http://127.0.0.1:7878",
        appType: "radarr",
        host: "http://127.0.0.1:7878",
        status: "healthy",
        imports: 1,
        medianHandoffMs: 4_800,
        p95HandoffMs: 4_800,
        queueCount: 0,
        awaitingCount: 0,
        hasWarnings: false,
        hasErrors: false,
        lastImportAtMs: t - 5 * 60 * 60_000,
        lastError: null,
      },
      {
        key: sonarr,
        name: "http://127.0.0.1:8989",
        appType: "sonarr",
        host: "http://127.0.0.1:8989",
        status: "healthy",
        imports: 12,
        medianHandoffMs: 40_000,
        p95HandoffMs: 71_000,
        queueCount: 0,
        awaitingCount: 0,
        hasWarnings: false,
        hasErrors: false,
        lastImportAtMs: t - 10 * 60 * 60_000,
        lastError: null,
      },
      {
        key: sonarr4k,
        name: "http://127.0.0.1:8990",
        appType: "sonarr",
        host: "http://127.0.0.1:8990",
        status: "degraded",
        imports: 29,
        medianHandoffMs: 41_000,
        p95HandoffMs: 138_000,
        queueCount: 3,
        awaitingCount: 3,
        hasWarnings: false,
        hasErrors: false,
        lastImportAtMs: t - 19 * 60_000,
        lastError: null,
      },
      {
        key: radarrUhd,
        name: "http://127.0.0.1:7879",
        appType: "radarr",
        host: "http://127.0.0.1:7879",
        status: "offline",
        imports: 0,
        medianHandoffMs: null,
        p95HandoffMs: null,
        queueCount: 0,
        awaitingCount: 0,
        hasWarnings: false,
        hasErrors: false,
        lastImportAtMs: null,
        lastError: "Unreachable",
      },
    ],
    awaiting: [
      {
        title: "Andor.S02E04.2160p.DSNP.WEB-DL",
        downloadId: "nzb-andor-s02e04",
        instanceKey: sonarr4k,
        instanceName: "http://127.0.0.1:8990",
        waitingMs: 47 * 60_000,
        isUnusual: true,
        trackedDownloadState: "importPending",
        statusReason: "Invalid data found when processing input",
      },
      {
        title: "Severance.S02E01.2160p.ATVP.WEB-DL",
        downloadId: "nzb-severance-s02e01",
        instanceKey: sonarr4k,
        instanceName: "http://127.0.0.1:8990",
        waitingMs: 12 * 60_000,
        isUnusual: false,
        trackedDownloadState: "importing",
        statusReason: null,
      },
      {
        title: "The.Bear.S03E01.1080p.WEB-DL",
        downloadId: "nzb-bear-s03e01",
        instanceKey: sonarr4k,
        instanceName: "http://127.0.0.1:8990",
        waitingMs: 4 * 60_000,
        isUnusual: false,
        trackedDownloadState: "importPending",
        statusReason: null,
      },
    ],
  };
}

export function mockArrHealthRequested(): boolean {
  if (typeof window === "undefined") return false;
  if (!import.meta.env.DEV) return false;
  return new URLSearchParams(window.location.search).has("mockArrHealth");
}
