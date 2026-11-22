import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { Link } from "react-router";
import { toast } from "sonner";
import { api, body, type Schemas } from "./api/client";
import { bytes, duration } from "./format";
import { Button } from "@/components/ui/button";
import { ConfirmButton, Failure, Loading } from "./ui";

/** Job states as the server names them. */
export const Par2State = {
  Queued: "queued",
  Running: "running",
  Available: "available",
  Verified: "verified",
  Repaired: "repaired",
  Deferred: "deferred",
  Damaged: "damaged",
  Unavailable: "unavailable",
  Failed: "failed",
  Cancelled: "cancelled",
} as const;

const PENDING: readonly string[] = [Par2State.Queued, Par2State.Running, Par2State.Deferred];

const pending = (job?: Schemas["Par2Job"] | null) => !!job && PENDING.includes(job.state);

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
      {job.state === Par2State.Deferred && <span>Retry after {new Date(job.next_at).toLocaleString()}</span>}
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
      void client.invalidateQueries({ queryKey: ["par2-jobs"] });
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

const describe = (job: Schemas["Par2Job"]) =>
  `${job.verify_only ? "Availability" : "PAR2 repair"} for ${job.file_path ?? job.id}: ${job.state}` +
  (job.reason ? ` — ${job.reason}` : "");

/** The latest change of a listed job's state or reason, for a polite live region. */
function useJobAnnouncement(jobs?: Schemas["Par2Job"][]) {
  const [seen, setSeen] = useState(jobs);
  const [announcement, setAnnouncement] = useState("");
  if (jobs !== seen) {
    setSeen(jobs);
    const before = new Map(seen?.map((job) => [job.id, describe(job)]));
    const changed = jobs?.find((job) => before.has(job.id) && before.get(job.id) !== describe(job));
    if (changed) setAnnouncement(describe(changed));
  }
  return announcement;
}

export function Par2Jobs() {
  const client = useQueryClient();
  const query = useQuery({
    queryKey: ["par2-jobs"],
    queryFn: () => body(api.GET("/api/v1/health/par2")),
    // Poll only while there is work to watch. The count and the list are separate reads.
    refetchInterval: (q) =>
      q.state.data?.enabled && (q.state.data.pending_jobs > 0 || q.state.data.jobs.some(pending))
        ? 5000
        : false,
  });
  const announcement = useJobAnnouncement(query.data?.jobs);
  const cancelJob = useMutation({
    mutationFn: (id: string) =>
      body(
        api.DELETE("/api/v1/health/par2-jobs/{id}", {
          params: { path: { id } },
        }),
      ),
    onSuccess: () => {
      toast.success("Cancellation requested");
      void client.invalidateQueries({ queryKey: ["par2-jobs"] });
      void client.invalidateQueries({ queryKey: ["par2"] });
    },
    onError: (error) => toast.error(error.message),
  });
  if (query.isPending) return <Loading />;
  if (query.isError) return <Failure error={query.error} />;
  const { enabled, jobs, pending_jobs, storage_used_bytes } = query.data;
  if (!enabled && jobs.length === 0) return null;
  return (
    <section className="mb-6 space-y-3" aria-label="Article checks and PAR2 repairs">
      <h2 className="font-medium">Article checks and repairs</h2>
      <p className="text-muted-foreground text-sm">
        Arr replacement comes first. PAR2 repair is the fallback, and degraded files are checked before they fail.
        Start a check or repair for one file in the Explorer.
      </p>
      {!enabled && (
        <p className="text-muted-foreground text-sm">
          Enable PAR2 repair in{" "}
          <Link className="underline" to="/settings/experiments">
            Settings → Experiments
          </Link>{" "}
          and restart first.
        </p>
      )}
      <p role="status" className="text-muted-foreground text-sm">
        {pending_jobs.toLocaleString()} pending jobs · {bytes(storage_used_bytes)} of saved repairs
      </p>
      <p role="status" className="sr-only">
        {announcement}
      </p>

      {jobs.length > 0 && (
        <div className="divide-y rounded-lg border" aria-label="Pending work and recent results">
          <p className="px-3 py-2 text-sm font-medium">Pending work and latest 20 results</p>
          {jobs.map((job) => (
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
              {job.state === Par2State.Running && (
                <progress
                  className="h-1 w-full accent-primary"
                  aria-label="Current job"
                  max={job.verify_only && job.total_articles > 0 ? job.total_articles : undefined}
                  value={job.verify_only && job.total_articles > 0 ? job.checked_articles : undefined}
                />
              )}
              {job.reason && <p className="break-words text-muted-foreground">{job.reason}</p>}
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
