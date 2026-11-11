import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link } from "react-router";
import { toast } from "sonner";
import { api, body, type Schemas } from "./api/client";
import { bytes, duration } from "./format";
import { Button } from "@/components/ui/button";
import { ConfirmButton, Failure, Loading } from "./ui";

const pending = (job?: Schemas["Par2Job"] | null) => !!job && ["queued", "running", "deferred"].includes(job.state);

function JobProgress({ job }: { job: Schemas["Par2Job"] }) {
  return (
    <>
      {job.transferred_bytes > 0 && <span>{bytes(job.transferred_bytes)} from providers</span>}
      {job.stored_bytes > 0 && <span>{bytes(job.stored_bytes)} saved</span>}
      {job.verified_bytes > 0 && <span>{bytes(job.verified_bytes)} verified</span>}
      {job.duration_ms > 0 && <span>{duration(Math.ceil(job.duration_ms / 1000))}</span>}
      <span>Attempts started: {job.attempts}</span>
      {job.state === "deferred" && <span>Retry after {new Date(job.next_at).toLocaleString()}</span>}
    </>
  );
}

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
                <JobProgress job={job} />
                {pending(job) && (
                  <Button
                    type="button"
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
    mutationFn: ({ cancel = false, repair = false }: { cancel?: boolean; repair?: boolean }) =>
      cancel
        ? body(api.DELETE("/api/v1/health/par2-scan"))
        : body(
            api.POST("/api/v1/health/par2-scan", {
              params: { query: { repair } },
            }),
          ),
    onSuccess: () => {
      void client.invalidateQueries({ queryKey: ["par2-scan"] });
    },
    onError: (error) => toast.error(error.message),
  });
  const cancelJob = useMutation({
    mutationFn: (id: string) =>
      body(
        api.DELETE("/api/v1/health/par2-jobs/{id}", {
          params: { path: { id } },
        }),
      ),
    onSuccess: () => {
      toast.success("Cancellation requested");
      void client.invalidateQueries({ queryKey: ["par2-scan"] });
      void client.invalidateQueries({ queryKey: ["par2"] });
    },
    onError: (error) => toast.error(error.message),
  });
  if (query.isPending) return <Loading />;
  if (query.isError) return <Failure error={query.error} />;
  const scan = query.data.scan;
  const disabled = !query.data.enabled || start.isPending || (!!scan && !scan.complete);
  const count = (state: number) => query.data.counts.find((c) => c.state === state)?.count ?? 0;
  return (
    <section className="mb-6 space-y-3" aria-label="PAR2 backlog and worker">
      <h2 className="font-medium">PAR2 checks and repairs</h2>
      <p className="text-muted-foreground text-sm">
        Reads complete source files, including whole archive volumes. This can use substantial provider traffic. Work
        survives restart. One job runs at a time; playback keeps priority.
      </p>
      <Link className="text-sm underline" to="/settings/repair">
        Configure worker limits and repair storage
      </Link>
      {!query.data.enabled && (
        <p className="text-muted-foreground text-sm">
          Enable PAR2 repair in{" "}
          <Link className="underline" to="/settings/experiments">
            Settings → Experiments
          </Link>{" "}
          and restart first.
        </p>
      )}
      <div className="flex flex-wrap items-center gap-2">
        <ConfirmButton
          variant="outline"
          label="Verify existing library"
          title="Verify the existing library?"
          message="Files are queued in small batches. This may read the whole library from your providers. Playback keeps priority."
          confirm="Start verification"
          disabled={disabled}
          onConfirm={() => start.mutate({})}
        />
        <ConfirmButton
          variant="outline"
          label="Check and repair existing library"
          title="Check and repair the existing library?"
          message="Queues source sets from the existing dataset in small batches. Checks complete source files and uses PAR2 to reconstruct missing articles when possible. Saves repaired articles in your config folder. Files without an original NZB are skipped."
          confirm="Queue checks and repairs"
          disabled={disabled}
          onConfirm={() => start.mutate({ repair: true })}
        />
        {scan && !scan.complete && (
          <Button
            type="button"
            variant="outline"
            size="sm"
            disabled={start.isPending}
            onClick={() => start.mutate({ cancel: true })}
          >
            Stop adding files
          </Button>
        )}
        <span role="status" className="text-muted-foreground text-sm">
          {scan
            ? `${scan.scanned_files.toLocaleString()}${scan.total_files != null ? ` of ${scan.total_files.toLocaleString()}` : ""} files considered for ${scan.repair ? "check and repair" : "verification"}; `
            : ""}
          {scan?.skipped_files ? `${scan.skipped_files.toLocaleString()} files skipped; ` : ""}
          {query.data.pending_jobs.toLocaleString()} pending jobs.{" "}
          {scan?.complete &&
            (scan.cancelled
              ? "Enumeration stopped."
              : query.data.pending_jobs > 0
                ? "Enumeration finished; queued work remains."
                : "Enumeration and queued work finished.")}
        </span>
      </div>
      {scan && !scan.complete && (
        <progress
          className="h-2 w-full accent-primary"
          aria-label="Library enumeration"
          max={Math.max(scan.total_files ?? 0, scan.scanned_files, 1)}
          value={scan.total_files == null ? undefined : scan.scanned_files}
        />
      )}
      <p className="text-muted-foreground text-xs">
        Stopping enumeration leaves already queued jobs running. Files from the same source set share a repair job.
        Enumeration counts files considered, including files that cannot be queued.
      </p>
      <div className="text-muted-foreground space-y-2 rounded-lg border p-3 text-sm">
        <p>
          {bytes(query.data.storage_used_bytes)} of {bytes(query.data.limits.storage_gb * 1_000_000_000)} repair storage
          used.
        </p>
        <p>
          {count(0)} queued · {count(1)} running · {count(4)} waiting to retry · {count(2)} verified · {count(3)}{" "}
          repaired · {count(5)} unavailable or failed · {count(6)} cancelled
        </p>
        <p className="text-xs">
          Totals include previous scans and manual jobs. Current limits: {query.data.limits.memory_mb} MB memory,{" "}
          {query.data.limits.transfer_gb} GB source reads and {query.data.limits.timeout_minutes} minutes per attempt;{" "}
          {query.data.limits.attempts} attempts. Saved limits apply at the next attempt.
        </p>
        {query.data.storage_used_bytes >= query.data.limits.storage_gb * 1_000_000_000 && (
          <p className="text-warning-text">
            Repair storage is full. Increase capacity to save new repairs. Referenced articles are retained.
          </p>
        )}
      </div>
      {query.data.jobs.length > 0 && (
        <div className="divide-y rounded-lg border" aria-label="Pending work and recent results">
          <p className="px-3 py-2 text-sm font-medium">Pending work and latest 20 results</p>
          {query.data.jobs.map((job) => (
            <div key={job.id} className="space-y-1 px-3 py-2 text-xs">
              <div className="flex flex-wrap items-center gap-2">
                {job.item_id ? (
                  <Link className="min-w-0 break-all underline" to={`/explorer?item=${job.item_id}`}>
                    {job.file_path ?? job.item_id}
                  </Link>
                ) : (
                  <span className="min-w-0 break-all">{job.file_path ?? `Source set / import ${job.id}`}</span>
                )}
                <span>
                  {job.verify_only ? "Full verification" : "PAR2 repair"}: {job.state}
                </span>
                {pending(job) && (
                  <Button
                    type="button"
                    variant="ghost"
                    size="sm"
                    disabled={cancelJob.isPending}
                    onClick={() => cancelJob.mutate(job.id)}
                  >
                    Cancel job
                  </Button>
                )}
              </div>
              {job.state === "running" && (
                <progress className="h-1 w-full accent-primary" aria-label="Current PAR2 job" />
              )}
              {job.reason && (
                <p role="status" className="break-words text-muted-foreground">
                  {job.reason}
                </p>
              )}
              <div className="text-muted-foreground flex flex-wrap gap-x-3 gap-y-1">
                <JobProgress job={job} />
              </div>
            </div>
          ))}
        </div>
      )}
    </section>
  );
}
