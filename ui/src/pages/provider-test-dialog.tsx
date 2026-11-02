import { useEffect, useRef } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Loader2 } from "lucide-react";
import { api, body, type Schemas } from "../api/client";
import { bytes } from "../format";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Failure, Status, type Tone } from "../ui";

type Job = Schemas["Job"];
type Run = Schemas["Run"];
type Sample = Schemas["Sample"];

const KEY = ["provider-tests"];

export function useProviderTests() {
  return useQuery({
    queryKey: KEY,
    queryFn: () => body(api.GET("/api/v1/provider-tests")),
    refetchInterval: (query) => (query.state.data?.job?.state === "running" ? 1000 : false),
  });
}

/** A speed test of one provider, or auto-tune of every enabled one when `id` is null; `view` shows the running test. */
export type TestTarget = { id: string | null; name?: string; view?: boolean };

/** Starts a speed test of one provider, or auto-tune of every enabled one when `id` is null. */
function useStartTest() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (id: string | null) =>
      body(
        id === null
          ? api.POST("/api/v1/providers/tune-pipeline")
          : api.POST("/api/v1/providers/{id}/speed-test", { params: { path: { id } } }),
      ),
    onSuccess: () => client.invalidateQueries({ queryKey: KEY }),
  });
}

const mbps = (rate: number) => `${rate.toFixed(1)} MB/s`;

const states: Record<Run["state"], [Tone, string]> = {
  pending: ["muted", "Waiting"],
  running: ["primary", "Running"],
  done: ["success", "Done"],
  failed: ["destructive", "Failed"],
  cancelled: ["warning", "Stopped"],
};

/** Explains a provider test, starts it, then shows progress and results; closing it stops a running test. */
export function ProviderTestDialog({ target, onClose }: { target: TestTarget | null; onClose: () => void }) {
  const client = useQueryClient();
  const tests = useProviderTests();
  const start = useStartTest();
  const cancel = useMutation({
    mutationFn: () => body(api.DELETE("/api/v1/provider-tests")),
    onSuccess: () => client.invalidateQueries({ queryKey: KEY }),
  });
  const ready = target !== null && !target.view && !start.isSuccess;
  const job = ready ? null : (tests.data?.job ?? null);
  const running = job?.state === "running";
  const tune = ready ? target.id === null : job?.kind === "tune";

  // Saved depths change the settings; refresh them once the job ends.
  const wasRunning = useRef(false);
  useEffect(() => {
    if (wasRunning.current && !running) void client.invalidateQueries({ queryKey: ["settings"] });
    wasRunning.current = running;
  }, [running, client]);

  const close = () => {
    if (running) cancel.mutate();
    start.reset();
    onClose();
  };

  return (
    <Dialog open={target !== null} onOpenChange={(next) => !next && close()}>
      <DialogContent className="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>{tune ? "Auto-tune pipeline" : ready && target.name ? `Speed test ${target.name}` : "Speed test"}</DialogTitle>
          <DialogDescription>
            {tune
              ? "Measures pipeline depths 1, 4, 8 and 16 on 4 connections per enabled provider for 13 seconds each and saves the fastest. Stops if playback starts."
              : "Downloads test articles at the provider's saved settings: 5 seconds to warm up, then 20 measured. Stops if playback starts."}{" "}
            The data counts against a provider's quota; a fast line can use several GB.
          </DialogDescription>
        </DialogHeader>
        {tests.isError && <Failure error={tests.error} />}
        {ready ? null : !job ? (
          <div className="text-muted-foreground flex items-center gap-2">
            <Loader2 className="size-4 animate-spin" />
            Starting…
          </div>
        ) : (
          <ul className="space-y-4">
            {job.providers.map((run) => (
              <RunView key={run.provider_id} job={job} run={run} />
            ))}
          </ul>
        )}
        {start.isError && <Failure error={start.error} />}
        {cancel.isError && <Failure error={cancel.error} />}
        <DialogFooter>
          {ready ? (
            <>
              <Button variant="outline" onClick={close}>
                Cancel
              </Button>
              <Button disabled={start.isPending} onClick={() => start.mutate(target.id)}>
                {start.isPending && <Loader2 className="animate-spin" />}
                {tune ? "Start auto-tune" : "Start speed test"}
              </Button>
            </>
          ) : running ? (
            <Button variant="outline" disabled={cancel.isPending} onClick={() => cancel.mutate()}>
              Stop
            </Button>
          ) : (
            <Button onClick={close}>Close</Button>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function RunView({ job, run }: { job: Job; run: Run }) {
  const [tone, label] = states[run.state];
  return (
    <li className="space-y-2">
      <div className="flex items-center justify-between gap-3">
        <span className="truncate font-medium">{run.name}</span>
        <Status tone={tone}>{label}</Status>
      </div>
      <p className="text-muted-foreground min-h-4 text-xs" aria-live="polite">
        {run.phase}
      </p>
      {job.kind === "speed" ? <SpeedResult run={run} /> : <DepthChart run={run} />}
      {run.error && <p className="text-destructive text-sm">{run.error}</p>}
    </li>
  );
}

function SpeedResult({ run }: { run: Run }) {
  const s = run.sample;
  if (!s) return null;
  const left = Math.max(0, Math.ceil((s.duration_ms - s.elapsed_ms) / 1000));
  return (
    <div className="flex flex-wrap items-baseline gap-x-4 gap-y-1">
      <span className={cn("font-mono text-lg font-medium tabular-nums", s.warming && "text-muted-foreground")}>{mbps(s.mb_per_s)}</span>
      <span className="text-muted-foreground text-xs tabular-nums">
        {bytes(s.bytes)} in {(s.elapsed_ms / 1000).toFixed(0)} s · {run.connections} connections · depth {s.depth}
        {s.failed > 0 && ` · ${s.failed} failed`}
        {run.state === "running" && (s.warming ? " · warming up" : ` · ${left} s left`)}
      </span>
    </div>
  );
}

const DEPTHS = [1, 4, 8, 16];

function DepthChart({ run }: { run: Run }) {
  const samples = new Map<number, Sample>(run.depths.map((s) => [s.depth, s]));
  const top = Math.max(1, ...run.depths.map((s) => s.mb_per_s));
  return (
    <div className="space-y-1.5">
      {DEPTHS.map((depth) => {
        const s = samples.get(depth);
        const best = run.recommended_depth === depth;
        return (
          <div key={depth} className="grid grid-cols-[4.5rem_1fr_6rem] items-center gap-2 text-xs">
            <span className="text-muted-foreground">Depth {depth}</span>
            <div className="bg-muted h-2 overflow-hidden rounded-full">
              <div
                className={cn("h-full rounded-full transition-[width]", best ? "bg-success" : "bg-primary/70")}
                style={{ width: `${s ? (s.mb_per_s / top) * 100 : 0}%` }}
              />
            </div>
            <span className={cn("text-right tabular-nums", best && "font-medium")}>{s ? mbps(s.mb_per_s) : "–"}</span>
          </div>
        );
      })}
      {run.recommended_depth != null && (
        <p className="text-sm">
          {run.applied
            ? run.recommended_depth === run.current_depth
              ? `Depth ${run.current_depth} is already the fastest.`
              : `Saved depth ${run.recommended_depth} (was ${run.current_depth}).`
            : `Best depth ${run.recommended_depth}; not saved.`}
        </p>
      )}
      <p className="text-muted-foreground text-xs">{run.connections} connections while testing</p>
    </div>
  );
}
