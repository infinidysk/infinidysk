import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { type FormEvent, type ReactNode, useEffect, useRef, useState } from "react";
import { CircleAlert, Loader2, RotateCcw } from "lucide-react";
import { useSearchParams } from "react-router";
import { api, body, HttpError, type Schemas } from "./api/client";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Failure, useTitle } from "./ui";
import { duration } from "./format";
import { switching, useSwitching } from "./database";

export const authKey = ["auth"];

export function useAuth() {
  return useQuery({ queryKey: authKey, queryFn: () => body(api.GET("/api/v1/auth")) });
}

export function Wordmark() {
  return (
    <span translate="no">
      infini<span className="text-primary">dysk</span>
    </span>
  );
}

export function Spinner() {
  return (
    <main className="grid min-h-screen place-items-center">
      <Loader2 className="text-muted-foreground size-6 animate-spin" />
    </main>
  );
}

/** Renders `children` once signed in; otherwise the setup or login form, or why the
 * import from C# failed. While the switch to PostgreSQL runs, its progress. */
export function Gate({ children }: { children: ReactNode }) {
  const client = useQueryClient();
  const since = useSwitching();
  const cutover = useQuery({
    queryKey: ["cutover"],
    queryFn: () => body(api.GET("/api/v1/database/cutover", { cache: "no-store" })),
    retry: false,
    refetchInterval: (query) => {
      const data = query.state.data;
      return since != null || !data || data.state === "running" ? 1000 : false;
    },
    refetchIntervalInBackground: true,
  });
  const upgrade = useQuery({
    queryKey: ["upgrade"],
    queryFn: () => body(api.GET("/api/v1/upgrade/status", { cache: "no-store" })),
    refetchInterval: (query) => {
      const state = query.state.data?.state;
      return !state || state === "importing" || state === "failed" ? 1000 : false;
    },
    refetchIntervalInBackground: true,
  });
  const cut = cutover.isError ? undefined : cutover.data;
  // The new process answers: the switch is over, on PostgreSQL or back on SQLite with the reason.
  const switched = since != null && cut?.state === "none" && cut.started_ms !== since;
  useEffect(() => {
    if (!switched) return;
    switching.end();
    void client.invalidateQueries();
  }, [switched, client]);
  if (cut?.state === "running") return <CutoverProgress progress={cut.progress ?? null} />;
  if (since != null && !switched) return <CutoverProgress progress={null} />;
  if (cutover.isPending) return <Spinner />;
  if (upgrade.isPending) return <Spinner />;
  if (upgrade.data?.state === "importing") return <UpgradeProgress status={upgrade.data} reconnecting={upgrade.isError} />;
  if (upgrade.data?.state === "failed") return <UpgradeFailed status={upgrade.data} />;
  if (upgrade.isError && !upgrade.data) return <main className="grid min-h-screen place-items-center p-4"><Failure error={upgrade.error} /></main>;
  return <Signed>{children}</Signed>;
}

type CountedStage = Exclude<Schemas["UpgradeStage"], "finishing">;
const upgradeStages: Record<CountedStage, { phase: string; done: string; total: string }> = {
  checking: { phase: "Checking 1.x files", done: "Files checked", total: "Files to check" },
  directories: { phase: "Writing folders", done: "Folders written", total: "Total folders" },
  nzbs: { phase: "Storing NZBs", done: "NZBs processed", total: "Total NZBs" },
  files: { phase: "Importing files", done: "Files written", total: "Total files" },
};
const upgradeSteps = Object.keys(upgradeStages) as CountedStage[];

function UpgradeProgress({ status, reconnecting }: { status: Schemas["UpgradeStatus"]; reconnecting: boolean }) {
  useTitle("Upgrading");
  const progress = status.progress;
  const stage = progress?.stage;
  const finishing = stage === "finishing";
  const labels = stage && stage !== "finishing" ? upgradeStages[stage] : undefined;
  const total = progress?.total;
  const completed = progress ? progress.done + progress.skipped : 0;
  const percent = finishing || total == null ? undefined : total > 0 ? Math.min(100, completed / total * 100) : 100;
  const step = stage && stage !== "finishing" ? upgradeSteps.indexOf(stage) + 1 : undefined;
  const phase = finishing ? "Finalizing import" : labels?.phase ?? "Preparing import";
  return (
    <main className="grid min-h-screen place-items-center p-4">
      <div className="w-full max-w-md space-y-6">
        <div className="flex flex-col items-center gap-3 text-center">
          <img src="/mark.svg" alt="" className="size-12" />
          <h1 className="text-xl font-medium">
            Upgrading <Wordmark />
          </h1>
        </div>
        <div className="space-y-5 border-y py-5">
          <div className="space-y-2">
            <div className="flex items-center justify-between gap-4 text-sm">
              <p role="status">
                {reconnecting ? "Connection lost. Reconnecting…" : phase}
                {!reconnecting && step ? <span className="text-muted-foreground"> · Step {step} of {upgradeSteps.length}</span> : null}
              </p>
              <span className="text-muted-foreground shrink-0 tabular-nums">{percent === undefined ? "" : `${percent.toFixed(1)}%`}</span>
            </div>
            <div
              className="bg-muted h-1.5 w-full overflow-hidden rounded-full"
              role="progressbar"
              aria-label={phase}
              aria-valuemin={0}
              aria-valuemax={100}
              aria-valuenow={percent}
            >
              <div
                className={`bg-primary h-full ${percent === undefined ? "motion-safe:animate-pulse" : ""}`}
                style={{ width: percent === undefined ? "33%" : `${percent}%` }}
              />
            </div>
          </div>
          {progress && labels ? (
            <dl className="space-y-3 text-sm">
              <div className="flex flex-wrap justify-between gap-x-4 gap-y-1">
                <dt className="text-muted-foreground">{labels.done}</dt>
                <dd className="font-mono tabular-nums">{progress.done.toLocaleString()}</dd>
              </div>
              <div className="flex flex-wrap justify-between gap-x-4 gap-y-1">
                <dt className="text-muted-foreground">{labels.total}</dt>
                <dd className="font-mono tabular-nums">{total == null ? "Counting…" : total.toLocaleString()}</dd>
              </div>
              {stage === "files" ? (
                <div className="flex flex-wrap justify-between gap-x-4 gap-y-1">
                  <dt className="text-muted-foreground">Skipped</dt>
                  <dd className="font-mono tabular-nums">{progress.skipped.toLocaleString()}</dd>
                </div>
              ) : null}
              <div className="flex flex-wrap justify-between gap-x-4 gap-y-1">
                <dt className="text-muted-foreground">Time left in this step</dt>
                <dd className="tabular-nums">{total != null && completed >= total ? "Finishing…" : progress.eta_seconds == null ? "Estimating…" : duration(progress.eta_seconds)}</dd>
              </div>
            </dl>
          ) : null}
        </div>
      </div>
    </main>
  );
}

const cutoverStages: Record<Schemas["ProgressStage"], string> = {
  copying: "Applying last changes",
  syncing: "Applying last changes",
  verifying: "Checking",
  switching: "Switching",
};

/** The switch to PostgreSQL; without `progress`, the old process is stopping or the new one is starting. */
function CutoverProgress({ progress }: { progress: Schemas["CutoverProgress"] | null }) {
  useTitle("Switching to PostgreSQL");
  const phase = progress ? cutoverStages[progress.stage] : "Restarting…";
  const percent = progress && progress.total > 0 ? Math.min(100, (progress.done / progress.total) * 100) : undefined;
  return (
    <main className="grid min-h-screen place-items-center p-4">
      <div className="w-full max-w-md space-y-6">
        <div className="flex flex-col items-center gap-3 text-center">
          <img src="/mark.svg" alt="" className="size-12" />
          <div>
            <h1 className="text-xl font-medium">Switching to PostgreSQL</h1>
            <p className="text-muted-foreground mt-1">Playback, WebDAV and imports pause until the switch finishes.</p>
          </div>
        </div>
        <div className="space-y-5 border-y py-5">
          <div className="space-y-2">
            <div className="flex items-center justify-between gap-4 text-sm">
              <p role="status">
                {phase}
                {progress?.table ? <span className="text-muted-foreground font-mono"> · {progress.table}</span> : null}
              </p>
              <span className="text-muted-foreground shrink-0 tabular-nums">{percent === undefined ? "" : `${percent.toFixed(1)}%`}</span>
            </div>
            <div
              className="bg-muted h-1.5 w-full overflow-hidden rounded-full"
              role="progressbar"
              aria-label={phase}
              aria-valuemin={0}
              aria-valuemax={100}
              aria-valuenow={percent}
            >
              <div
                className={`bg-primary h-full ${percent === undefined ? "motion-safe:animate-pulse" : ""}`}
                style={{ width: percent === undefined ? "33%" : `${percent}%` }}
              />
            </div>
          </div>
        </div>
      </div>
    </main>
  );
}

function Signed({ children }: { children: ReactNode }) {
  const auth = useAuth();
  if (auth.isPending) return <Spinner />;
  if (auth.isError) {
    return (
      <main className="grid min-h-screen place-items-center p-4">
        <Failure error={new Error(`Cannot reach the server: ${auth.error.message}`)} />
      </main>
    );
  }
  if (!auth.data.required) return children;
  if (auth.data.setup_required) return <Credentials setup />;
  if (!auth.data.user) return <Credentials setup={false} status={auth.data} />;
  return children;
}

/** The import from C# failed: nothing but this page is served until a retry succeeds. */
function UpgradeFailed({ status }: { status: Schemas["UpgradeStatus"] }) {
  const client = useQueryClient();
  const retry = useMutation({
    mutationFn: async () => {
      const { response } = await api.POST("/api/v1/upgrade/retry");
      if (!response.ok) throw new Error(response.statusText);
    },
    onSettled: () => client.invalidateQueries(),
  });
  useTitle("Upgrade failed");
  return (
    <main className="grid min-h-screen place-items-center p-4">
      <div className="w-full max-w-2xl space-y-6">
        <div className="flex flex-col items-center gap-3 text-center">
          <img src="/mark.svg" alt="" className="size-12" />
          <div>
            <h1 className="text-xl font-medium tracking-tight">Upgrading from your C# install failed</h1>
            <p className="text-muted-foreground mt-1">
              Your C# data was not changed. Until the import succeeds, the download client and WebDAV answer “unavailable”,
              so your Arrs and media servers wait instead of seeing an empty library.
            </p>
          </div>
        </div>
        <Alert variant="destructive">
          <CircleAlert />
          <AlertTitle>What went wrong</AlertTitle>
          <AlertDescription>
            <pre className="font-mono text-xs break-words whitespace-pre-wrap">{status.error}</pre>
          </AlertDescription>
        </Alert>
        <div className="flex flex-col items-center gap-2">
          <Button onClick={() => retry.mutate()} disabled={retry.isPending}>
            {retry.isPending ? <Loader2 className="animate-spin" /> : <RotateCcw />}
            {retry.isPending ? "Importing…" : "Retry the import"}
          </Button>
          {retry.isError && <Failure error={retry.error} />}
          <p className="text-muted-foreground text-center text-xs">
            Fix the cause first, for example a provider that cannot be reached or a missing folder. A large install takes a
            few minutes; restarting also retries.
          </p>
        </div>
      </div>
    </main>
  );
}

function credentialsError(error: Error, setup: boolean): string {
  if (!(error instanceof HttpError)) return "Cannot reach the server. Check that infinidysk is running, then try again.";
  if (error.status === 401 && !setup) return "Wrong username or password. The username is case-sensitive.";
  if (error.status === 409) return "An admin account already exists. Reload the page to sign in.";
  const message = error.message.charAt(0).toUpperCase() + error.message.slice(1);
  return error.status === 400 || error.status === 403 ? `${message}.` : `${message}. Check the server log for details.`;
}

function Credentials({ setup, status }: { setup: boolean; status?: Schemas["AuthStatus"] }) {
  const client = useQueryClient();
  const passwordRef = useRef<HTMLInputElement>(null);
  const [params, setParams] = useSearchParams();
  // Read once, then dropped from the address bar so a reload does not repeat it.
  const [outcome] = useState(() => params.get("sso"));
  useEffect(() => {
    if (params.has("sso")) {
      setParams(
        (p) => {
          p.delete("sso");
          return p;
        },
        { replace: true },
      );
    }
  }, [params, setParams]);
  const passwordLogin = setup || !!status?.password_login;
  const sso = !setup && !!status?.sso;
  const [username, setUsername] = useState(setup ? "admin" : "");
  const [password, setPassword] = useState("");
  const [confirm, setConfirm] = useState("");
  const mismatch = setup && confirm !== "" && confirm !== password;
  const submit = useMutation({
    mutationFn: () => {
      const login = { body: { username, password } };
      return body(setup ? api.POST("/api/v1/auth/setup", login) : api.POST("/api/v1/auth/login", login));
    },
    onSuccess: () => client.invalidateQueries(),
    // The disabled submit button drops focus; hand it back to the field most likely to need fixing.
    onError: () => {
      passwordRef.current?.focus();
      passwordRef.current?.select();
    },
  });
  const wrong = !setup && submit.error instanceof HttpError && submit.error.status === 401;
  useTitle(setup ? "Set up" : "Sign in");
  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    if (setup && confirm !== password) return;
    submit.mutate();
  };

  return (
    <main className="grid min-h-screen place-items-center p-4">
      <form onSubmit={onSubmit} className="w-full max-w-sm space-y-6">
        <div className="flex flex-col items-center gap-3 text-center">
          <img src="/mark.svg" alt="" className="size-12" />
          <div>
            <h1 className="text-xl font-medium tracking-tight">
              {setup ? "Welcome to" : "Sign in to"} <Wordmark />
            </h1>
            <p className="text-muted-foreground mt-1">
              {setup
                ? "Create the admin account to finish setting up."
                : sso && passwordLogin
                  ? "Use single sign-on or the admin account."
                  : sso
                    ? "Use your identity provider."
                    : "Use the admin account created during setup."}
            </p>
          </div>
        </div>
        <div className="bg-card space-y-4 rounded-lg border p-5">
          {outcome === "failed" && (
            <Alert variant="destructive">
              <CircleAlert />
              <AlertDescription>
                SSO sign-in failed. Try again{passwordLogin ? ", or use your password" : ""}. The server log says why.
              </AlertDescription>
            </Alert>
          )}
          {outcome === "off" && (
            <Alert>
              <CircleAlert />
              <AlertDescription>SSO is not set up. Sign in with your password.</AlertDescription>
            </Alert>
          )}
          {passwordLogin && (
            <>
              <div className="space-y-2">
                <Label htmlFor="username">Username</Label>
                <Input
                  id="username"
                  autoComplete="username"
                  autoCapitalize="none"
                  autoCorrect="off"
                  spellCheck={false}
                  required
                  autoFocus
                  aria-invalid={wrong || undefined}
                  aria-describedby={wrong ? "credentials-error" : undefined}
                  value={username}
                  onChange={(e) => setUsername(e.target.value)}
                />
              </div>
              <div className="space-y-2">
                <Label htmlFor="password">Password</Label>
                <Input
                  ref={passwordRef}
                  id="password"
                  type="password"
                  autoComplete={setup ? "new-password" : "current-password"}
                  minLength={setup ? 8 : undefined}
                  required
                  aria-invalid={wrong || undefined}
                  aria-describedby={wrong ? "credentials-error" : undefined}
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                />
                {setup && <p className="text-muted-foreground text-xs">At least 8 characters.</p>}
              </div>
              {setup && (
                <div className="space-y-2">
                  <Label htmlFor="confirm-password">Confirm password</Label>
                  <Input
                    id="confirm-password"
                    type="password"
                    autoComplete="new-password"
                    required
                    aria-invalid={mismatch}
                    aria-describedby={mismatch ? "confirm-password-error" : undefined}
                    value={confirm}
                    onChange={(e) => setConfirm(e.target.value)}
                  />
                  {mismatch && (
                    <p id="confirm-password-error" className="text-destructive text-xs">
                      Passwords do not match.
                    </p>
                  )}
                </div>
              )}
              <Button className="w-full" disabled={submit.isPending || mismatch}>
                {submit.isPending && <Loader2 className="animate-spin" />}
                {submit.isPending
                  ? setup
                    ? "Creating account…"
                    : "Signing in…"
                  : setup
                    ? "Create account"
                    : "Sign in"}
              </Button>
              {submit.isError && (
                <div id="credentials-error">
                  <Failure error={new Error(credentialsError(submit.error, setup))} />
                </div>
              )}
            </>
          )}
          {sso && (
            <>
              {passwordLogin && (
                <div className="text-muted-foreground flex items-center gap-3 text-xs">
                  <span className="bg-border h-px flex-1" />
                  or
                  <span className="bg-border h-px flex-1" />
                </div>
              )}
              <Button asChild className="w-full" variant={passwordLogin ? "outline" : "default"}>
                <a href="/auth/oidc/login">Sign in with SSO</a>
              </Button>
            </>
          )}
        </div>
      </form>
    </main>
  );
}
