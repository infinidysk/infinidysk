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
      {job.transferred_bytes > 0 && <span>{bytes(job.transferred_bytes)} {job.verify_only ? "read for checks" : "from providers"}</span>}
      {job.verify_only && job.total_articles > 0 && <span>{job.checked_articles.toLocaleString()} of {job.total_articles.toLocaleString()} articles checked</span>}
      {!job.verify_only && job.completed_requests > 0 && <span>{job.completed_requests.toLocaleString()} provider reads finished · {job.missing_requests.toLocaleString()} missing</span>}
      {job.stored_bytes > 0 && <span>{bytes(job.stored_bytes)} saved</span>}
      {job.verified_bytes > 0 && <span>{bytes(job.verified_bytes)} verified</span>}
      {job.duration_ms > 0 && <span>{duration(Math.ceil(job.duration_ms / 1000))}</span>}
      <span>Attempts used: {job.attempts}</span>
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
    onSuccess: (_, { cancel, verify }) => {
      toast.success(cancel ? "Cancellation requested" : verify ? "Availability check queued" : "PAR2 repair queued");
      void client.invalidateQueries({ queryKey: ["par2", id] });
      void client.invalidateQueries({ queryKey: ["file", id] });
    },
    onError: (error) => toast.error(error.message),
  });
  if (status.isError) return <Failure error={status.error} />;
  const enabled = status.data?.enabled ?? false;
  return (
    <section className="space-y-2" aria-label="Article availability and PAR2 repair">
      <div className="flex flex-wrap gap-2">
        <ConfirmButton
          variant="outline"
          label="Check availability"
          title="Check article availability?"
          message="Checks every article with small availability requests and a few body spot checks. It does not download the whole file or verify every byte."
          confirm="Check availability"
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
          Reconstruction needs the original NZB. Availability checks need no PAR2 proof.
        </p>
      )}
      <div role="status" className="text-muted-foreground space-y-1 text-xs">
        {([false, true] as const).map((verify) => {
          const job = verify ? status.data?.verification : status.data?.repair;
          return (
            job && (
              <div key={String(verify)} className="flex flex-wrap items-center gap-2">
                <span>
                  {verify ? "Availability" : "PAR2 repair"}: {job.state}
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
  const count = (state: number, verify?: boolean, damaged?: boolean) => {
    const group = query.data.counts.find((c) => c.state === state);
    if (!group) return 0;
    if (verify === undefined) return group.count;
    if (!verify) return group.count - group.checks;
    if (damaged === undefined) return group.checks;
    return damaged ? group.damaged_checks : group.checks - group.damaged_checks;
  };
  return (
    <section className="mb-6 space-y-3" aria-label="PAR2 backlog and worker">
      <h2 className="font-medium">Article checks and repairs</h2>
      <p className="text-muted-foreground text-sm">
        Checks article availability with a few body spot checks. Damaged sets can queue PAR2 repair separately. Work
        survives restart. Availability and repair each have a worker; availability pauses for playback.
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
          label="Check existing library"
          title="Check availability across the library?"
          message="Checks every stored article for availability, with a few body spot checks per file. This is not full byte integrity verification."
          confirm="Start availability checks"
          disabled={disabled}
          onConfirm={() => start.mutate({})}
        />
        <ConfirmButton
          variant="outline"
          label="Check and repair existing library"
          title="Check and repair the existing library?"
          message="Checks availability first and queues PAR2 repair only when damage is found. Repairs can read complete source sets. Files without an original NZB are skipped."
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
            ? `${scan.scanned_files.toLocaleString()}${scan.total_files != null ? ` of ${scan.total_files.toLocaleString()}` : ""} files considered for ${scan.repair ? "check and repair" : "availability"}; `
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
        Stopping enumeration leaves already queued jobs running. Scan jobs share article observations. Manual requests receive priority, with regular turns for scan work. Files from the same source set share a repair job.
        Enumeration counts files considered, including files that cannot be queued.
      </p>
      <div className="text-muted-foreground space-y-2 rounded-lg border p-3 text-sm">
        <p>
          {bytes(query.data.storage_used_bytes)} of {bytes(query.data.limits.storage_gb * 1_000_000_000)} repair storage
          used.
        </p>
        <p>
          {count(0)} queued · {count(1)} running · {count(4)} waiting to retry · {count(6)} cancelled
        </p>
        <p>
          Availability: {count(2, true)} available · {count(5, true, true)} found damage · {count(5, true, false)} could not check
        </p>
        <p>
          PAR2: {count(3, false)} repaired · {count(2, false)} verified without repair · {count(5, false)} could not repair
        </p>
        <p className="text-xs">
          Totals count jobs across previous scans and manual requests, not unique files. A file can have both a check and a repair result. Current limits: {query.data.limits.memory_mb} MB memory,{" "}
          {query.data.limits.transfer_gb} GB source reads, {query.data.limits.check_concurrency} availability requests and {query.data.limits.timeout_minutes} minutes per attempt;{" "}
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
                  {job.verify_only ? "Availability" : "PAR2 repair"}: {job.state}
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
                <progress className="h-1 w-full accent-primary" aria-label="Current job"
                  max={job.verify_only && job.total_articles > 0 ? job.total_articles : undefined}
                  value={job.verify_only && job.total_articles > 0 ? job.checked_articles : undefined} />
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
