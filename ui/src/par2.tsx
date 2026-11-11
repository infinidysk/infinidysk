import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "react-router";
import { toast } from "sonner";
import { api, body, type Schemas } from "./api/client";
import { bytes, duration } from "./format";
import { Button } from "@/components/ui/button";
import { ConfirmButton, Failure } from "./ui";

const pending = (job?: Schemas["Par2Job"] | null) => !!job && ["queued", "running", "deferred"].includes(job.state);

export function Par2Actions({ id, hasNzb }: { id: string; hasNzb: boolean }) {
  const client = useQueryClient();
  const status = useQuery({
    queryKey: ["par2", id],
    queryFn: () => body(api.GET("/api/v1/files/{id}/par2", { params: { path: { id } } })),
    refetchInterval: (q) => (pending(q.state.data?.repair) || pending(q.state.data?.verification) ? 2000 : false),
  });
  const act = useMutation({
    mutationFn: ({ verify, cancel = false }: { verify: boolean; cancel?: boolean }) => {
      const path = verify ? "/api/v1/files/{id}/verify" : "/api/v1/files/{id}/par2";
      return cancel
        ? body(api.DELETE(path, { params: { path: { id } } }))
        : body(api.POST(path, { params: { path: { id } } }));
    },
    onSuccess: (_, { cancel }) => {
      toast.success(cancel ? "Cancellation requested" : "PAR2 work queued");
      void client.invalidateQueries({ queryKey: ["par2", id] });
      void client.invalidateQueries({ queryKey: ["file", id] });
    },
    onError: (error) => toast.error(error.message),
  });
  if (status.isError) return <Failure error={status.error} />;
  const enabled = status.data?.enabled ?? false;
  return (
    <section className="space-y-2" aria-label="PAR2 verification and repair">
      <div className="flex flex-wrap gap-2">
        <ConfirmButton
          variant="outline"
          label="Verify with PAR2"
          title="Verify complete source files?"
          message="This can read complete source files and archive volumes from your providers. It records a full content check separately from sampled health checks."
          confirm="Verify with PAR2"
          disabled={!enabled || act.isPending || pending(status.data?.verification)}
          onConfirm={() => act.mutate({ verify: true })}
        />
        <ConfirmButton
          variant="outline"
          label="Repair with PAR2"
          title="Repair original articles?"
          message="This can read a complete recovery set and saves verified replacement articles in your config folder. Existing library paths stay in place."
          confirm="Repair with PAR2"
          disabled={!enabled || !hasNzb || act.isPending || pending(status.data?.repair)}
          onConfirm={() => act.mutate({ verify: false })}
        />
      </div>
      {!enabled && (
        <p className="text-muted-foreground text-xs">
          Enable PAR2 repair in{" "}
          <Link className="underline" to="/settings/experiments">
            Settings → Experiments
          </Link>{" "}
          and restart first.
        </p>
      )}
      {!hasNzb && (
        <p className="text-muted-foreground text-xs">
          Reconstruction needs the original NZB. Full verification can use a complete stored proof.
        </p>
      )}
      <div role="status" className="text-muted-foreground space-y-1 text-xs">
        {([false, true] as const).map((verify) => {
          const job = verify ? status.data?.verification : status.data?.repair;
          return (
            job && (
              <div key={String(verify)} className="flex flex-wrap items-center gap-2">
                <span>
                  {verify ? "Full verification" : "PAR2 repair"}: {job.state}
                  {job.reason && ` — ${job.reason}`}
                </span>
                {job.transferred_bytes > 0 && <span>{bytes(job.transferred_bytes)} from providers</span>}
                {job.stored_bytes > 0 && <span>{bytes(job.stored_bytes)} saved</span>}
                {job.verified_bytes > 0 && <span>{bytes(job.verified_bytes)} verified</span>}
                {job.duration_ms > 0 && <span>{duration(Math.ceil(job.duration_ms / 1000))}</span>}
                {pending(job) && (
                  <Button
                    variant="ghost"
                    size="sm"
                    disabled={act.isPending}
                    onClick={() => act.mutate({ verify, cancel: true })}
                  >
                    Cancel
                  </Button>
                )}
              </div>
            )
          );
        })}
      </div>
    </section>
  );
}

export function Par2Scan() {
  const client = useQueryClient();
  const query = useQuery({
    queryKey: ["par2-scan"],
    queryFn: () => body(api.GET("/api/v1/health/par2-scan")),
    refetchInterval: 5000,
  });
  const start = useMutation({
    mutationFn: (cancel: boolean) =>
      cancel ? body(api.DELETE("/api/v1/health/par2-scan")) : body(api.POST("/api/v1/health/par2-scan")),
    onSuccess: () => {
      void client.invalidateQueries({ queryKey: ["par2-scan"] });
    },
    onError: (error) => toast.error(error.message),
  });
  if (!query.data?.enabled) return null;
  const scan = query.data.scan;
  return (
    <section className="mb-6 space-y-2">
      <h2 className="font-medium">Full library verification</h2>
      <p className="text-muted-foreground text-sm">
        Reads complete source files, including whole archive volumes. This can use substantial provider traffic.
        Completed checks survive restart.
      </p>
      <div className="flex flex-wrap items-center gap-2">
        <ConfirmButton
          variant="outline"
          label="Verify existing library"
          title="Verify the existing library?"
          message="Files are queued in small batches. This may read the whole library from your providers. Playback keeps priority."
          confirm="Start verification"
          disabled={start.isPending || (!!scan && !scan.complete)}
          onConfirm={() => start.mutate(false)}
        />
        {scan && !scan.complete && (
          <Button variant="outline" size="sm" disabled={start.isPending} onClick={() => start.mutate(true)}>
            Stop adding files
          </Button>
        )}
        <span role="status" className="text-muted-foreground text-sm">
          {scan ? `${scan.scanned_files.toLocaleString()} files considered; ` : ""}
          {query.data.pending_jobs.toLocaleString()} pending jobs.{" "}
          {scan?.complete && (scan.cancelled ? "Enumeration stopped." : "Enumeration finished.")}
        </span>
      </div>
      <p className="text-muted-foreground text-xs">
        Stopping enumeration leaves already queued jobs running. Cancel an individual check in its file details.
      </p>
    </section>
  );
}
