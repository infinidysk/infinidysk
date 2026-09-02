import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useBlocker, useFetcher, useNavigate, useOutletContext } from "react-router";
import { z } from "zod";
import type { Route } from "./+types/route";
import { BackendApiError, backendClient } from "~/clients/backend-client.server";
import { IS_FRONTEND_AUTH_DISABLED, getSessionUser } from "~/auth/authentication.server";
import type { AppOutletContext } from "~/auth/authorization";
import {
  Alert,
  Button,
  Icon,
  ManagedEnvProvider,
  Spinner,
  type ManagedEnvMap,
} from "~/components/ui";
import { ConfirmModal } from "~/components/confirm-modal/confirm-modal";
import { withUrlBase } from "~/utils/url-base";
import {
  SETUP_CONFIG_KEYS,
  SETUP_DEFAULT_CONFIG,
  changedSetupConfig,
  createInitialDraft,
  normalizeStrategy,
  safeReturnTo,
  validateSetupStep,
  type SetupDraft,
} from "./setup-model";
import {
  BackupStep,
  IngestionStep,
  LibraryDirectoryStep,
  LibraryTypeStep,
  PlaybackStep,
  ReviewStep,
  SETUP_STEPS,
  SetupProgress,
} from "./setup-steps";

type SetupActionData =
  | { ok: true; intent: "skip" }
  | { ok: true; intent: "complete"; restartRequired: boolean; changedConfigKeys: string[] }
  | { ok: false; error: string; fieldErrors?: Record<string, string[]> };

const completionSchema = z.object({
  strategy: z.enum(["symlinks", "strm"]),
  ingestionMethods: z.array(z.enum(["arrs", "search", "manual"])).min(1),
  config: z.record(z.string(), z.string()),
});

export async function loader({ request }: Route.LoaderArgs) {
  const url = new URL(request.url);
  const [state, configItems] = await Promise.all([
    backendClient.getSetupWizardState(),
    backendClient.getConfig([...SETUP_CONFIG_KEYS]),
  ]);
  const config = { ...SETUP_DEFAULT_CONFIG };
  const managedEnv: ManagedEnvMap = {};
  for (const item of configItems) {
    config[item.configName] = item.configValue;
    if (item.environmentVariableName) managedEnv[item.configName] = item.environmentVariableName;
  }
  const sessionUser = await getSessionUser(request);

  return {
    state,
    config,
    managedEnv,
    returnTo: safeReturnTo(url.searchParams.get("returnTo")),
    isReadOnly: !IS_FRONTEND_AUTH_DISABLED && sessionUser?.role === "readonly",
    mainDatabaseProvider: state.mainDatabaseProvider,
  };
}

export async function action({ request }: Route.ActionArgs): Promise<SetupActionData> {
  const sessionUser = await getSessionUser(request);
  if (!IS_FRONTEND_AUTH_DISABLED && sessionUser?.role !== "admin") {
    return { ok: false, error: "Administrator access is required to change setup." };
  }

  const formData = await request.formData();
  const intent = formData.get("intent");
  try {
    if (intent === "skip") {
      await backendClient.skipSetupWizard();
      return { ok: true, intent: "skip" };
    }
    if (intent !== "complete") {
      return { ok: false, error: "Unknown setup action." };
    }

    const parsed = completionSchema.safeParse({
      strategy: formData.get("strategy"),
      ingestionMethods: parseJson(formData.get("ingestionMethods")),
      config: parseJson(formData.get("config")),
    });
    if (!parsed.success) return { ok: false, error: "Review the setup fields and try again." };

    const result = await backendClient.completeSetupWizard(parsed.data);
    return { ok: true, intent: "complete", ...result };
  } catch (error) {
    if (error instanceof BackendApiError) {
      return {
        ok: false,
        error: error.detail,
        ...(error.fieldErrors ? { fieldErrors: error.fieldErrors } : {}),
      };
    }
    return {
      ok: false,
      error: error instanceof Error ? error.message : "Setup could not be saved.",
    };
  }
}

export default function SetupRoute({ loaderData }: Route.ComponentProps) {
  const { role } = useOutletContext<AppOutletContext>();
  const isReadOnly = loaderData.isReadOnly || role === "readonly";
  const navigate = useNavigate();
  const fetcher = useFetcher<typeof action>();
  const [step, setStep] = useState(0);
  const [draft, setDraft] = useState<SetupDraft>(() =>
    createInitialDraft(
      loaderData.config,
      loaderData.managedEnv,
      loaderData.state.ingestionMethods,
      loaderData.state.setupRequired,
    ),
  );
  const [attempted, setAttempted] = useState(false);
  const [strategyChangeConfirmed, setStrategyChangeConfirmed] = useState(false);
  const [completion, setCompletion] = useState<null | {
    restartRequired: boolean;
    changedConfigKeys: string[];
  }>(null);
  const allowNavigationRef = useRef(false);
  const baselineStrategy = normalizeStrategy(loaderData.config["api.import-strategy"]);
  const strategy = normalizeStrategy(draft.config["api.import-strategy"]);
  const changes = useMemo(
    () => changedSetupConfig(loaderData.config, draft.config, loaderData.managedEnv),
    [draft.config, loaderData.config, loaderData.managedEnv],
  );
  const isDirty =
    Object.keys(changes).length > 0 ||
    JSON.stringify(draft.ingestionMethods) !== JSON.stringify(loaderData.state.ingestionMethods);
  const errors = validateSetupStep(
    step,
    draft,
    loaderData.managedEnv,
    strategyChangeConfirmed,
    baselineStrategy,
  );
  const busy = fetcher.state !== "idle";

  const updateDraft = useCallback((updater: (current: SetupDraft) => SetupDraft) => {
    setDraft((current) => updater(current));
  }, []);

  useEffect(() => {
    document.querySelector<HTMLElement>("[data-setup-heading]")?.focus();
  }, [step]);

  useEffect(() => {
    const data = fetcher.data;
    if (!data?.ok) return;
    if (data.intent === "skip") {
      allowNavigationRef.current = true;
      void navigate(loaderData.returnTo, { replace: true });
    } else {
      allowNavigationRef.current = true;
      setCompletion({
        restartRequired: data.restartRequired,
        changedConfigKeys: data.changedConfigKeys,
      });
    }
  }, [fetcher.data, loaderData.returnTo, navigate]);

  useEffect(() => {
    const onBeforeUnload = (event: BeforeUnloadEvent) => {
      if (!isDirty || completion || allowNavigationRef.current) return;
      event.preventDefault();
    };
    window.addEventListener("beforeunload", onBeforeUnload);
    return () => window.removeEventListener("beforeunload", onBeforeUnload);
  }, [completion, isDirty]);

  const blocker = useBlocker(
    ({ currentLocation, nextLocation }) =>
      !allowNavigationRef.current &&
      !completion &&
      isDirty &&
      currentLocation.pathname !== nextLocation.pathname,
  );

  const continueForward = () => {
    setAttempted(true);
    if (errors.length > 0) return;
    setStep((current) => Math.min(SETUP_STEPS.length - 1, current + 1));
    setAttempted(false);
  };

  const submitComplete = () => {
    setAttempted(true);
    if (errors.length > 0) return;
    const data = new FormData();
    data.set("intent", "complete");
    data.set("strategy", strategy);
    data.set("ingestionMethods", JSON.stringify(draft.ingestionMethods));
    data.set("config", JSON.stringify(changes));
    void fetcher.submit(data, { method: "POST" });
  };

  if (completion) {
    return (
      <main className="mx-auto flex w-full max-w-4xl flex-col gap-6 px-4 py-6 md:px-8 md:py-10">
        <section className="space-y-5 border-y border-base-content/10 py-10 text-center">
          <Icon name="task_alt" className="mx-auto !text-[52px] text-success" />
          <div>
            <h1 className="text-3xl font-bold">Setup guide complete</h1>
            <p className="mx-auto mt-2 max-w-[65ch] text-sm text-base-content/60">
              Your {strategy === "symlinks" ? "Symlinks" : "STRM"} configuration has been saved.
            </p>
          </div>
          {completion.restartRequired && (
            <Alert variant="warning" className="alert-soft mx-auto max-w-2xl text-left text-sm">
              <Icon name="restart_alt" className="!text-[20px]" />
              <span>
                Restart InfiniDysk before testing playback so the Segment Cache change takes effect.
              </span>
            </Alert>
          )}
          <div className="flex flex-wrap justify-center gap-3">
            <Button
              variant="primary"
              onClick={() => void navigate(loaderData.returnTo, { replace: true })}
            >
              Continue to InfiniDysk
              <Icon name="arrow_forward" className="!text-[18px]" />
            </Button>
            <a className="btn" href={withUrlBase("/queue")}>
              Upload a test NZB
            </a>
          </div>
        </section>
      </main>
    );
  }

  return (
    <ManagedEnvProvider value={loaderData.managedEnv}>
      <main className="mx-auto flex w-full max-w-5xl flex-col gap-7 px-4 py-5 md:px-8 md:py-8">
        <header className="flex flex-wrap items-start justify-between gap-4 border-b border-base-content/10 pb-5">
          <div>
            <h1 className="text-3xl font-bold text-base-content">Setup Guide</h1>
            <p className="mt-2 max-w-[65ch] text-sm text-base-content/60">
              Configure the playback path, ingestion, backups, and library health for this
              installation.
            </p>
          </div>
          {!loaderData.state.setupRequired && (
            <Button variant="ghost" onClick={() => void navigate(loaderData.returnTo)}>
              <Icon name="close" className="!text-[18px]" />
              Close
            </Button>
          )}
        </header>

        <SetupProgress step={step} />

        {isReadOnly && (
          <Alert variant="info" className="alert-soft text-sm">
            <Icon name="lock" className="!text-[20px]" />
            <span>
              Read-only users can review this guide, but an administrator must apply changes.
            </span>
          </Alert>
        )}

        <fieldset
          disabled={isReadOnly || busy}
          className="min-w-0 border-y border-base-content/10 py-7"
        >
          {step === 0 && (
            <LibraryTypeStep
              draft={draft}
              managedEnv={loaderData.managedEnv}
              updateDraft={updateDraft}
            />
          )}
          {step === 1 && <PlaybackStep draft={draft} updateDraft={updateDraft} />}
          {step === 2 && <IngestionStep draft={draft} updateDraft={updateDraft} />}
          {step === 3 && (
            <BackupStep
              draft={draft}
              updateDraft={updateDraft}
              mainDatabaseProvider={loaderData.mainDatabaseProvider}
            />
          )}
          {step === 4 && <LibraryDirectoryStep draft={draft} updateDraft={updateDraft} />}
          {step === 5 && (
            <ReviewStep
              baseline={loaderData.config}
              draft={draft}
              changes={changes}
              managedEnv={loaderData.managedEnv}
              strategyChangeConfirmed={strategyChangeConfirmed}
              setStrategyChangeConfirmed={setStrategyChangeConfirmed}
            />
          )}
        </fieldset>

        {attempted && errors.length > 0 && (
          <Alert variant="danger" className="alert-soft items-start text-sm">
            <Icon name="error" className="!text-[20px]" />
            <ul className="list-disc space-y-1 pl-4">
              {errors.map((error) => (
                <li key={error}>{error}</li>
              ))}
            </ul>
          </Alert>
        )}
        {fetcher.data && !fetcher.data.ok && (
          <Alert variant="danger" className="alert-soft text-sm">
            <Icon name="error" className="!text-[20px]" />
            <span>{fetcher.data.error}</span>
          </Alert>
        )}

        <footer className="flex flex-wrap items-center justify-between gap-3">
          <div className="flex flex-wrap gap-2">
            {step > 0 && (
              <Button
                onClick={() => {
                  setStep((current) => current - 1);
                  setAttempted(false);
                }}
              >
                <Icon name="arrow_back" className="!text-[18px]" />
                Back
              </Button>
            )}
            {loaderData.state.setupRequired && !isReadOnly && (
              <fetcher.Form method="POST">
                <input type="hidden" name="intent" value="skip" />
                <Button type="submit" variant="ghost" disabled={busy}>
                  Skip setup
                </Button>
              </fetcher.Form>
            )}
          </div>
          {!isReadOnly && step < SETUP_STEPS.length - 1 && (
            <Button variant="primary" onClick={continueForward}>
              Continue
              <Icon name="arrow_forward" className="!text-[18px]" />
            </Button>
          )}
          {!isReadOnly && step === SETUP_STEPS.length - 1 && (
            <Button variant="primary" onClick={submitComplete} disabled={busy}>
              {busy ? <Spinner size="sm" /> : <Icon name="check" className="!text-[18px]" />}
              {busy ? "Applying…" : "Apply setup"}
            </Button>
          )}
        </footer>
      </main>

      <ConfirmModal
        show={blocker.state === "blocked"}
        title="Leave setup guide?"
        message="Your staged setup changes have not been applied."
        cancelText="Stay"
        confirmText="Leave"
        onCancel={() => blocker.state === "blocked" && blocker.reset()}
        onConfirm={() => blocker.state === "blocked" && blocker.proceed()}
      />
    </ManagedEnvProvider>
  );
}

function parseJson(value: FormDataEntryValue | null): unknown {
  if (typeof value !== "string") return null;
  try {
    return JSON.parse(value) as unknown;
  } catch {
    return null;
  }
}
