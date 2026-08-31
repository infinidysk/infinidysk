import type { ReactNode } from "react";
import { Badge, Icon, Tooltip } from "~/components/ui";

export type StatusBadgeProps = {
  className?: string | undefined;
  status: string;
  percentage?: string | undefined;
  error?: string | undefined;
};

export function StatusBadge({ className, status, percentage, error }: StatusBadgeProps) {
  const statusLower = status?.toLowerCase();

  if (statusLower === "completed") {
    return <StatusShell className="badge-success badge-xs">{statusLower}</StatusShell>;
  }

  if (statusLower === "failed" || statusLower == "upload failed") {
    if (error?.startsWith("Article with message-id")) error = "Missing articles";

    return (
      <Tooltip content={error || "Upload failed"} className="z-50">
        <StatusShell className="badge-error badge-xs cursor-help">
          {statusLower === "upload failed" && <Icon name="upload" className="!text-[12px]" />}
          failed
        </StatusShell>
      </Tooltip>
    );
  }

  if (statusLower === "downloading") {
    const percentNum = Number(percentage);
    const badgeText = `${percentNum > 100 ? percentNum - 100 : percentNum}%`;
    const isHealthChecking = percentNum > 100;
    const downloadValue = percentNum >= 0 ? Math.min(percentNum, 100) : 0;
    const healthValue = isHealthChecking ? Math.min(percentNum - 100, 100) : 0;

    return (
      <ProgressStatus className={className}>
        <Badge className="badge-sm w-full justify-center font-semibold">{badgeText}</Badge>
        <progress
          className={`progress h-1 w-full ${isHealthChecking ? "progress-neutral" : "progress-primary"}`}
          value={downloadValue}
          max={100}
        />
        {isHealthChecking && (
          <progress
            className="progress progress-success h-1 w-full"
            value={healthValue}
            max={100}
          />
        )}
      </ProgressStatus>
    );
  }

  if (statusLower === "uploading") {
    const percentNum = Number(percentage);
    const badgeText = `${percentNum}%`;

    return (
      <ProgressStatus className={className}>
        <Badge className="badge-info badge-sm w-full justify-center gap-0.5 font-semibold">
          <Icon name="upload" className="!text-[12px]" />
          {badgeText}
        </Badge>
        <progress
          className="progress progress-info h-1 w-full"
          value={Math.min(percentNum, 100)}
          max={100}
        />
      </ProgressStatus>
    );
  }

  if (statusLower === "pending") {
    return (
      <StatusShell>
        <Icon name="upload" className="!text-[12px]" />
        pending
      </StatusShell>
    );
  }

  if (statusLower === "health-checking") {
    const percentNum = Number(percentage);
    const badgeText = `${percentNum}%`;

    return (
      <ProgressStatus className={className}>
        <Badge className="badge-sm w-full justify-center font-semibold">{badgeText}</Badge>
        <progress
          className="progress progress-success h-1 w-full"
          value={Math.min(percentNum, 100)}
          max={100}
        />
      </ProgressStatus>
    );
  }

  return <StatusShell>{statusLower}</StatusShell>;
}

function StatusShell({ className = "", children }: { className?: string; children: ReactNode }) {
  return (
    <Badge
      className={`inline-flex w-[85px] items-center justify-center gap-0.5 font-semibold ${className}`}
    >
      {children}
    </Badge>
  );
}

function ProgressStatus({
  className = "",
  children,
}: {
  className?: string | undefined;
  children: ReactNode;
}) {
  return <div className={`inline-flex w-[85px] flex-col gap-0.5 ${className}`}>{children}</div>;
}
