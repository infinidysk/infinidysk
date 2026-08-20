import { useState } from "react";
import { Icon } from "~/components/ui/icon";
import { useWebsocketTopic } from "~/utils/shared-websocket";

type MigrateDatabaseFilesToBlobstoreProps = {
  savedConfig: Record<string, string>;
};

export function MigrateDatabaseFilesToBlobstore(_props: MigrateDatabaseFilesToBlobstoreProps) {
  const [progress, setProgress] = useState<string | null>(null);

  useWebsocketTopic("uftbmp", "state", setProgress, {
    onClose: () => setProgress(null),
  });

  return (
    <div className="space-y-4">
      <p className="text-sm leading-relaxed text-base-content/70">
        Move large blobs out of SQLite and into the filesystem to improve database read and write
        performance.
      </p>

      <div className="rounded-lg border border-base-content/10 bg-base-200/40 p-3">
        <div className="flex items-start gap-3">
          <span className="rounded-lg bg-primary/10 p-2 text-primary">
            <Icon name="database" className="!text-[20px]" />
          </span>
          <div className="min-w-0">
            <p className="text-sm font-medium text-base-content">Automatic background migration</p>
            <p
              aria-live="polite"
              className="mt-1 whitespace-pre-line break-words font-mono text-xs text-base-content/65"
            >
              {progress || "Waiting for the migration to start."}
            </p>
          </div>
        </div>
      </div>

      <div
        className="flex items-start gap-2 rounded-lg bg-base-200/30 px-3 py-2.5 text-xs leading-relaxed text-base-content/55"
        id="blob-task-progress-help"
      >
        <Icon name="info" className="mt-0.5 !text-[17px] shrink-0 text-base-content/45" />
        <p>
          <span className="font-medium text-base-content/70">No action is required.</span> The
          backend runs this optimization automatically. Read more about{" "}
          <a
            className="link link-primary"
            href="https://sqlite.org/intern-v-extern-blob.html"
            rel="noreferrer"
            target="_blank"
          >
            SQLite blob storage
          </a>
          .
        </p>
      </div>
    </div>
  );
}
