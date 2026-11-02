import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { type FormEvent, type ReactNode, useRef, useState } from "react";
import { CircleAlert, Loader2, RotateCcw } from "lucide-react";
import { api, body, HttpError, type Schemas } from "./api/client";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Failure, useTitle } from "./ui";
import { duration } from "./format";

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
 * import from C# failed. */
export function Gate({ children }: { children: ReactNode }) {
  const upgrade = useQuery({
    queryKey: ["upgrade"],
    queryFn: () => body(api.GET("/api/v1/upgrade/status", { cache: "no-store" })),
    refetchInterval: (query) => {
      const state = query.state.data?.state;
      return !state || state === "importing" || state === "failed" ? 1000 : false;
    },
    refetchIntervalInBackground: true,
  });
  if (upgrade.isPending) return <Spinner />;
  if (upgrade.data?.state === "importing") return <UpgradeProgress status={upgrade.data} reconnecting={upgrade.isError} />;
  if (upgrade.data?.state === "failed") return <UpgradeFailed status={upgrade.data} />;
  if (upgrade.isError && !upgrade.data) return <main className="grid min-h-screen place-items-center p-4"><Failure error={upgrade.error} /></main>;
  return <Signed>{children}</Signed>;
}

function UpgradeProgress({ status, reconnecting }: { status: Schemas["UpgradeStatus"]; reconnecting: boolean }) {
  useTitle("Upgrading");
  const progress = status.progress;
  const completed = progress ? progress.files + progress.skipped : 0;
  const percent = progress ? (progress.total > 0 ? Math.min(100, completed / progress.total * 100) : 100) : undefined;
  const finishing = progress && completed >= progress.total;
  const phase = finishing ? "Finalizing import" : progress ? "Importing files" : "Preparing import";
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
              <p role="status">{reconnecting ? "Connection lost. Reconnecting..." : phase}</p>
              <span className="text-muted-foreground shrink-0 tabular-nums">{percent === undefined ? "" : `${percent.toFixed(1)}%`}</span>
            </div>
            <div
              className="bg-muted h-1.5 w-full overflow-hidden rounded-full"
              role="progressbar"
              aria-label="File processing"
              aria-valuemin={0}
              aria-valuemax={100}
              aria-valuenow={percent}
            >
              <div className="bg-primary h-full" style={{ width: `${percent ?? 0}%` }} />
            </div>
          </div>
          <dl className="space-y-3 text-sm">
            <div className="flex flex-wrap justify-between gap-x-4 gap-y-1">
              <dt className="text-muted-foreground">Files written</dt>
              <dd className="font-mono tabular-nums">{(progress?.files ?? 0).toLocaleString()}</dd>
            </div>
            <div className="flex flex-wrap justify-between gap-x-4 gap-y-1">
              <dt className="text-muted-foreground">Total files</dt>
              <dd className="font-mono tabular-nums">{progress?.total.toLocaleString() ?? "Counting..."}</dd>
            </div>
            <div className="flex flex-wrap justify-between gap-x-4 gap-y-1">
              <dt className="text-muted-foreground">Skipped</dt>
              <dd className="font-mono tabular-nums">{(progress?.skipped ?? 0).toLocaleString()}</dd>
            </div>
            <div className="flex flex-wrap justify-between gap-x-4 gap-y-1">
              <dt className="text-muted-foreground">Estimated remaining</dt>
              <dd className="tabular-nums">{finishing ? "Finalizing..." : progress?.eta_seconds == null ? "Estimating..." : duration(progress.eta_seconds)}</dd>
            </div>
          </dl>
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
  if (!auth.data.user) return <Credentials setup={false} />;
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
  return error.status === 400 ? `${message}.` : `${message}. Check the server log for details.`;
}

function Credentials({ setup }: { setup: boolean }) {
  const client = useQueryClient();
  const passwordRef = useRef<HTMLInputElement>(null);
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
              {setup ? "Create the admin account to finish setting up." : "Use the admin account created during setup."}
            </p>
          </div>
        </div>
        <div className="bg-card space-y-4 rounded-lg border p-5">
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
        </div>
      </form>
    </main>
  );
}
