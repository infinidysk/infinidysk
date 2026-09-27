import { Alert, Badge, Button, Icon, Modal } from "~/components/ui";
import { formatFileSize } from "~/utils/file-size";
import type { LibraryCatalogItem, LibraryFileDetails } from "~/clients/backend-client.server";

export type LibraryModalActionState = "idle" | "pending";

export type LibraryModalFeedback = {
  variant: "success" | "danger";
  message: string;
} | null;

export type LibraryFileModalProps = {
  item: LibraryCatalogItem;
  quality: "4k" | "1080p" | "720p" | "sd" | "unknown";
  cachePercentage: number | null;
  details: LibraryFileDetails | null;
  detailsLoading: boolean;
  detailsError: string | null;
  previewUrl: string | null;
  canPrewarm: boolean;
  actionState: LibraryModalActionState;
  feedback: LibraryModalFeedback;
  onClose: () => void;
  onPreview: () => void;
  onRunHealthCheck: () => void;
  onRequeue: () => void;
  onPrewarm: () => void;
};

export function LibraryFileModal(props: LibraryFileModalProps) {
  const { item, details, detailsLoading, detailsError, previewUrl, canPrewarm } = props;
  const latest = details?.latestHealth ?? null;
  const downloadUrl = previewUrl
    ? `${previewUrl}${previewUrl.includes("?") ? "&" : "?"}download=true`
    : null;
  const qualityLabel =
    props.quality === "4k" ? "4K" : props.quality === "unknown" ? "Unknown" : props.quality;
  const sourcePath = details?.contentPath ?? item.contentPath ?? item.mappings[0]?.targetText;

  return (
    <Modal open title={item.displayName} onClose={props.onClose} size="wide">
      <div className="flex flex-col gap-5">
        <div className="rounded-2xl border border-primary/20 bg-gradient-to-br from-primary/15 via-base-200 to-base-200 p-5">
          <div className="flex flex-wrap items-start gap-4">
            <div className="flex h-14 w-14 shrink-0 items-center justify-center rounded-2xl bg-primary/20 text-primary">
              <Icon name="movie" className="!text-[30px]" />
            </div>
            <div className="min-w-0 flex-1">
              <p className="text-xs font-semibold uppercase tracking-wide text-base-content/55">
                Library file
              </p>
              <p className="mt-1 break-all text-lg font-semibold leading-snug">
                {item.displayName}
              </p>
              <div className="mt-3 flex flex-wrap gap-2">
                <Badge>{item.health}</Badge>
                <Badge>{qualityLabel}</Badge>
                <Badge>{item.kind === "internal" ? "InfiniDysk" : "External"}</Badge>
              </div>
            </div>
          </div>
        </div>

        <div className="grid gap-3 sm:grid-cols-4">
          <Fact
            label="File size"
            value={item.size != null ? formatFileSize(item.size) : "Unknown"}
          />
          <Fact label="Quality" value={qualityLabel} />
          <Fact
            label="Native Cache"
            value={props.cachePercentage == null ? "Unavailable" : `${props.cachePercentage}%`}
          />
          <Fact label="Mappings" value={String(item.mappingCount)} />
        </div>

        {details ? (
          <div className="grid gap-3 sm:grid-cols-3">
            <Fact label="Release date" value={formatDate(details.releaseDate)} />
            <Fact label="Last health check" value={formatDate(details.lastHealthCheck)} />
            <Fact
              label="Next health check"
              value={
                details.healthRepairPending ? "Repair pending" : formatDate(details.nextHealthCheck)
              }
            />
          </div>
        ) : null}

        {sourcePath ? (
          <section className="rounded-xl border border-base-content/10 bg-base-200 p-4">
            <h3 className="text-xs font-semibold uppercase tracking-wide text-base-content/55">
              Source path
            </h3>
            <p className="mt-2 break-all font-mono text-xs leading-relaxed text-base-content/75">
              {sourcePath}
            </p>
          </section>
        ) : null}

        {detailsLoading && (
          <p role="status" className="text-sm text-base-content/60">
            Loading file details…
          </p>
        )}
        {detailsError && (
          <Alert variant="danger" role="alert">
            {detailsError}
          </Alert>
        )}

        <div className="flex flex-wrap gap-2 rounded-xl border border-base-content/10 bg-base-200 p-3">
          {previewUrl && (
            <Button
              size="small"
              onClick={props.onPreview}
              disabled={props.actionState === "pending"}
            >
              <Icon name="play_arrow" className="!text-[16px]" />
              Preview
            </Button>
          )}
          {downloadUrl && (
            <a className="btn btn-sm gap-2" href={downloadUrl}>
              <Icon name="download" className="!text-[16px]" />
              Download
            </a>
          )}
          {item.kind === "internal" && item.davItemId && (
            <>
              <Button
                variant="outline"
                size="small"
                onClick={props.onRunHealthCheck}
                disabled={props.actionState === "pending"}
              >
                <Icon name="favorite" className="!text-[16px]" />
                Run health check
              </Button>
              <Button
                variant="outline"
                size="small"
                onClick={props.onRequeue}
                disabled={props.actionState === "pending"}
              >
                <Icon name="refresh" className="!text-[16px]" />
                Requeue repair
              </Button>
              {canPrewarm ? (
                <Button
                  variant="outline"
                  size="small"
                  onClick={props.onPrewarm}
                  disabled={props.actionState === "pending"}
                >
                  <Icon name="bolt" className="!text-[16px]" />
                  Prewarm
                </Button>
              ) : null}
            </>
          )}
        </div>
        {!canPrewarm && item.kind === "internal" && (
          <p className="text-xs text-base-content/60">
            Prewarm is unavailable: Native cache is inactive.
          </p>
        )}
        <p className="text-xs text-base-content/60">
          Run health check queues all due checks, not just this file.
        </p>

        {props.feedback && (
          <Alert
            variant={props.feedback.variant}
            role={props.feedback.variant === "danger" ? "alert" : "status"}
          >
            {props.feedback.message}
          </Alert>
        )}

        <section className="rounded-xl border border-base-content/10 bg-base-200 p-4">
          <h3 className="text-sm font-semibold">
            Mappings <span className="font-normal text-base-content/50">({item.mappingCount})</span>
          </h3>
          <ul className="mt-3 flex flex-col gap-2">
            {item.mappings.map((m) => (
              <li
                key={m.linkPath}
                className="rounded-lg border border-base-content/10 bg-base-300/50 p-3 text-sm"
              >
                <div className="mb-2 flex flex-wrap gap-2">
                  <Badge>{m.mappingType}</Badge>
                  <Badge>{m.status}</Badge>
                </div>
                <p className="break-all font-medium">{m.linkPath}</p>
                <p className="mt-1 break-all font-mono text-xs text-base-content/55">
                  → {m.targetText}
                </p>
              </li>
            ))}
          </ul>
        </section>

        <section className="rounded-xl border border-base-content/10 bg-base-200 p-4">
          <h3 className="text-sm font-semibold">Health history</h3>
          {latest ? (
            <p className="mt-2 text-sm">
              {latest.result} · {latest.repairStatus} ·{" "}
              {new Date(latest.createdAt).toLocaleString()}
              {latest.message ? ` — ${latest.message}` : ""}
            </p>
          ) : (
            <p className="mt-2 text-sm text-base-content/60">
              No health checks recorded for this file.
            </p>
          )}
          <a className="link mt-3 inline-block text-sm" href="/health">
            Open Health
          </a>
        </section>
      </div>
    </Modal>
  );
}

function Fact({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-xl border border-base-content/10 bg-base-200 p-4">
      <p className="text-xs font-semibold uppercase tracking-wide text-base-content/55">{label}</p>
      <p className="mt-1 text-base font-semibold">{value}</p>
    </div>
  );
}

function formatDate(value: string | null | undefined): string {
  if (!value) return "—";
  const date = new Date(value);
  return Number.isNaN(date.valueOf()) ? "—" : date.toLocaleDateString();
}
