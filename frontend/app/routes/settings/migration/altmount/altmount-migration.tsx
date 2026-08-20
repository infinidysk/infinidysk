import { useCallback, useEffect, useRef, useState } from "react";
import { Alert, Badge, Spinner, Tooltip } from "~/components/ui/feedback";
import { Button } from "~/components/ui/button";
import { Input, Select, Toggle } from "~/components/ui/form";
import { Icon } from "~/components/ui/icon";
import { SettingsIntro } from "~/components/ui";
import { ConfirmModal } from "~/components/confirm-modal/confirm-modal";
import { Modal } from "~/components/ui/modal";
import {
  type AltmountPathDetection,
  type CategoryMapRow,
  type CollisionGroup,
  type ConnectForm,
  type ReleaseFilters,
  type ReleaseRow,
  type SessionStatus,
  type SubmissionIssue,
  type SummaryResponse,
  type SymlinkFilters,
  type SymlinkBackupInfo,
  type SymlinkPlanForm,
  type SymlinkRow,
  DEFAULT_ALTMOUNT_ROOT,
  canConnectMigration,
  canEditCategoryMappings,
  canEditReleaseSelection,
  canResetMigration,
  canStartScanMigration,
  connectFormWithDetectedPaths,
  connectFormWithStatusPaths,
  hasScanData,
  inferStandardAltmountRoot,
  isMigrationWorkActive,
  loadTableLatest,
  requestAltmountPathDetection,
  useAltmountMigration,
} from "./use-altmount-migration";

function useDebouncedValue<T>(value: T, delayMs: number): T {
  const [debounced, setDebounced] = useState(value);
  useEffect(() => {
    const handle = window.setTimeout(() => setDebounced(value), delayMs);
    return () => window.clearTimeout(handle);
  }, [value, delayMs]);
  return debounced;
}

const STEPS = ["Connect", "Categories", "Scan", "Review", "Run", "Links"] as const;

const LINK_STEP = 5;

const SYMLINK_STATUS_HELP: Record<string, string> = {
  rewrite: "Points to Altmount and has a verified InfiniDysk replacement.",
  orphan: "Points to Altmount, but no safe InfiniDysk match was found.",
  unreadable:
    "Found in the library, but its target could not be read or classified. It remains unchanged and may still point at Altmount.",
  "already-nzbdav": "Already points to InfiniDysk, so no change is needed.",
  "not-altmount": "Does not point to Altmount and will be left unchanged.",
  applied: "Successfully repointed to InfiniDysk.",
  failed: "A rewrite was attempted but could not be completed.",
  removed: "The orphaned Altmount symlink was removed after its original target was backed up.",
};

const SYMLINK_STATUS_LABELS: Record<string, string> = {
  rewrite: "Rewrite",
  orphan: "Orphan",
  unreadable: "Unreadable",
  "already-nzbdav": "InfiniDysk",
  "not-altmount": "Other",
  applied: "Applied",
  failed: "Failed",
  removed: "Removed",
};

const MATCH_METHODS: Record<string, { label: string; help: string }> = {
  provenance: {
    label: "migration database",
    help: "Matched using a file mapping saved from a completed migration.",
  },
  "relative-path": {
    label: "relative path",
    help: "Matched by a unique normalized path within the release.",
  },
  exact: {
    label: "exact",
    help: "Matched by a unique normalized filename.",
  },
  "unique-size": {
    label: "unique size",
    help: "The names differed, but the file size uniquely identified the target.",
  },
  "single-leaf-fallback": {
    label: "single file",
    help: "Matched because the release had only one source file and one InfiniDysk file.",
  },
};

/** True once the migration has finished, so the optional Links step is available. */
function canLinkStep(status: SessionStatus | undefined): boolean {
  return (
    status === "complete" ||
    status === "linking" ||
    status === "linked" ||
    status === "applying" ||
    status === "removing_orphans" ||
    status === "restoring"
  );
}

function stepForStatus(status: SessionStatus | undefined): number {
  switch (status) {
    case "connected":
      return 1;
    case "mapped":
      return 2;
    case "scanning":
      return 2;
    case "scan_cancelling":
      return 2;
    case "scanned":
      return 3;
    case "running":
    case "paused":
    case "cancelling":
    case "complete":
    case "cancelled":
      return 4;
    // Step 6 is opt-in: it does not auto-advance from "complete", but once the
    // user enters it, all Step 6 operation statuses live on the Links step.
    case "linking":
    case "linked":
    case "applying":
    case "removing_orphans":
    case "restoring":
      return LINK_STEP;
    default:
      return 0; // idle
  }
}

export function AltmountMigration() {
  const m = useAltmountMigration();
  const sessionStatus = m.status?.sessionStatus;
  const natural = stepForStatus(sessionStatus);
  const [viewStep, setViewStep] = useState(natural);

  // Follow the workflow forward as the backend advances; the user can still
  // click back to any reached step.
  useEffect(() => setViewStep(stepForStatus(sessionStatus)), [sessionStatus]);

  return (
    <div className="flex w-full flex-col gap-6">
      <SettingsIntro>
        Import an existing Altmount library into InfiniDysk by re-submitting each release through
        InfiniDysk's own download pipeline. Connect to the library, map categories, scan and review,
        then run the migration. Nothing in your current InfiniDysk content is modified. This path is
        experimental — verify a few releases play correctly before decommissioning AltMount.
      </SettingsIntro>

      <ul className="steps w-full text-xs">
        {STEPS.map((label, idx) => {
          // The optional Links step is reachable once the migration completes,
          // even though "complete" naturally rests on Run.
          const reachable = idx <= natural || (idx === LINK_STEP && canLinkStep(sessionStatus));
          return (
            <li key={label} className={`step ${reachable ? "step-primary" : ""}`}>
              <button
                type="button"
                className={`bg-transparent ${reachable ? "cursor-pointer" : "cursor-default opacity-60"}`}
                disabled={!reachable}
                onClick={() => {
                  if (reachable) setViewStep(idx);
                }}
                onKeyDown={(event) => {
                  if (!reachable) return;
                  if (event.key === "Enter" || event.key === " ") {
                    event.preventDefault();
                    setViewStep(idx);
                  }
                }}
              >
                {label}
              </button>
            </li>
          );
        })}
      </ul>

      {m.error && (
        <Alert className="alert-soft text-sm" variant="danger">
          <Icon name="error" className="!text-[18px]" />
          {m.error}
        </Alert>
      )}

      {viewStep === 0 && <ConnectStep m={m} />}
      {viewStep === 1 && <CategoriesStep m={m} onDone={() => setViewStep(2)} />}
      {viewStep === 2 && <ScanStep m={m} onReview={() => setViewStep(3)} />}
      {viewStep === 3 && <ReviewStep m={m} onRun={() => setViewStep(4)} />}
      {viewStep === 4 && <RunStep m={m} onLinks={() => setViewStep(LINK_STEP)} />}
      {viewStep === LINK_STEP && <SymlinkStep m={m} />}

      <ResetFooter m={m} />
    </div>
  );
}

type Hook = ReturnType<typeof useAltmountMigration>;

// --- Step 1: connect -------------------------------------------------------

function ConnectStep({ m }: { m: Hook }) {
  const roots = m.status?.roots;
  const [advancedMode, setAdvancedMode] = useState(false);
  const [basicRoot, setBasicRoot] = useState(DEFAULT_ALTMOUNT_ROOT);
  const [detection, setDetection] = useState<AltmountPathDetection | null>(null);
  const [detectionError, setDetectionError] = useState<string | null>(null);
  const [detecting, setDetecting] = useState(false);
  const detectionGeneration = useRef(0);
  const initialDetectionStarted = useRef(false);
  const [form, setForm] = useState<ConnectForm>({
    metadataRoot: roots?.altmountMetadataRoot ?? "",
    configPath: roots?.altmountConfigPath ?? "",
    storeRoot: roots?.altmountStoreRoot ?? "",
    maxQueueDepth: m.status?.maxQueueDepth ?? 20,
    submitWorkers: m.status?.submitWorkers ?? 1,
  });

  const detectPaths = useCallback(async (candidateRoot: string) => {
    const generation = ++detectionGeneration.current;
    setDetecting(true);
    setDetection(null);
    setDetectionError(null);
    try {
      const result = await requestAltmountPathDetection(candidateRoot);
      if (generation !== detectionGeneration.current) return;
      setDetection(result);
      setBasicRoot(result.root);
      if (result.detected) setForm((current) => connectFormWithDetectedPaths(current, result));
    } catch (error) {
      if (generation !== detectionGeneration.current) return;
      setDetectionError(error instanceof Error ? error.message : String(error));
    } finally {
      if (generation === detectionGeneration.current) setDetecting(false);
    }
  }, []);

  // Invalidate any in-flight detection on unmount so stale setState calls are ignored.
  useEffect(
    () => () => {
      detectionGeneration.current++;
    },
    [],
  );

  // Wait for status so saved standard-layout paths take precedence over the default.
  useEffect(() => {
    if (!m.status || initialDetectionStarted.current) return;
    initialDetectionStarted.current = true;

    const savedRoot = inferStandardAltmountRoot(m.status.roots);
    const hasSavedPaths = Boolean(
      m.status.roots.altmountMetadataRoot ||
      m.status.roots.altmountConfigPath ||
      m.status.roots.altmountStoreRoot,
    );
    if (hasSavedPaths && !savedRoot) {
      setDetectionError(
        "Your saved paths use a non-standard layout. Enter a standard Altmount data directory below, or turn on Advanced mode to keep editing them.",
      );
      return;
    }

    const candidateRoot = savedRoot ?? DEFAULT_ALTMOUNT_ROOT;
    setBasicRoot(candidateRoot);
    void detectPaths(candidateRoot);
  }, [m.status, detectPaths]);

  // Sync once the initial status loads.
  useEffect(() => {
    if (!m.status) return;
    setForm((f) => ({
      ...f,
      metadataRoot: f.metadataRoot || (m.status?.roots.altmountMetadataRoot ?? ""),
      configPath: f.configPath || (m.status?.roots.altmountConfigPath ?? ""),
      storeRoot: f.storeRoot || (m.status?.roots.altmountStoreRoot ?? ""),
      maxQueueDepth: m.status?.maxQueueDepth ?? f.maxQueueDepth,
      submitWorkers: m.status?.submitWorkers ?? f.submitWorkers,
    }));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [m.status?.sessionStatus]);

  const canSubmit =
    (advancedMode ? form.metadataRoot.trim().length > 0 : detection?.detected === true) &&
    canConnectMigration(m.status?.sessionStatus) &&
    m.busy !== "connect";

  const toggleAdvancedMode = (enabled: boolean) => {
    setAdvancedMode(enabled);
    if (enabled && detecting) {
      detectionGeneration.current++;
      setDetecting(false);
    }
  };

  const connect = () => {
    const values =
      !advancedMode && detection?.detected ? connectFormWithDetectedPaths(form, detection) : form;
    void m.connect(values);
  };

  return (
    <Section
      icon="link"
      title="Connect to Altmount"
      subtitle="Detect the recommended single-mount layout, or configure each path manually."
    >
      <div className="space-y-4">
        <div className="flex justify-end">
          <Toggle
            id="altmount-connect-advanced"
            className="cursor-pointer gap-2"
            checked={advancedMode}
            onChange={(event) => toggleAdvancedMode(event.target.checked)}
            label={<span className="text-sm font-medium text-base-content">Advanced mode</span>}
          />
        </div>

        {advancedMode ? (
          <>
            <PathField
              label="Altmount Metadata Root"
              required
              help="Directory containing Altmount's .meta files (the virtual-file metadata tree)."
              value={form.metadataRoot}
              onChange={(v) => setForm({ ...form, metadataRoot: v })}
            />
            <PathField
              label="Path to Altmount config.yaml"
              help="Altmount config file — read to discover SABnzbd categories. Optional but recommended."
              value={form.configPath}
              onChange={(v) => setForm({ ...form, configPath: v })}
            />
            <PathField
              label="Altmount Store Root"
              help="Directory holding the .nzbs/ store tree. Used to locate stores when the recorded path differs."
              value={form.storeRoot}
              onChange={(v) => setForm({ ...form, storeRoot: v })}
            />
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
              <NumberField
                label="Max Queue Depth"
                help="Upper bound on releases queued into InfiniDysk at once."
                value={form.maxQueueDepth}
                min={1}
                max={500}
                onChange={(v) => setForm({ ...form, maxQueueDepth: v })}
              />
              <NumberField
                label="Submit Workers"
                help="Recommended to keep at 1 — concurrent submissions can trip queue-key eviction."
                value={form.submitWorkers}
                min={1}
                max={16}
                onChange={(v) => setForm({ ...form, submitWorkers: v })}
              />
            </div>
          </>
        ) : detecting ? (
          <div className="flex items-center gap-3 rounded-lg border border-base-content/10 bg-base-200/30 p-4 text-sm text-base-content/65">
            <Spinner className="h-4 w-4" />
            <span>
              Checking the Altmount layout at <span className="font-mono">{basicRoot}</span>
            </span>
          </div>
        ) : detection?.detected ? (
          <div className="space-y-3 rounded-lg border border-success/25 bg-success/5 p-4">
            <div className="flex flex-wrap items-center justify-between gap-3">
              <div className="flex items-center gap-2 text-sm font-medium text-success">
                <Icon name="check_circle" className="!text-[20px]" />
                Altmount paths detected
              </div>
              <Button
                variant="ghost"
                size="xsmall"
                onClick={() => {
                  detectionGeneration.current++;
                  setDetecting(false);
                  setDetection(null);
                  setDetectionError(null);
                  setForm((current) => connectFormWithStatusPaths(current, m.status?.roots));
                }}
              >
                Change directory
              </Button>
            </div>
            <dl className="grid grid-cols-1 gap-2 text-xs sm:grid-cols-[auto_1fr]">
              <dt className="text-base-content/50">Metadata root</dt>
              <dd className="break-all font-mono text-base-content">{detection.metadataRoot}</dd>
              <dt className="text-base-content/50">config.yaml</dt>
              <dd className="break-all font-mono text-base-content">{detection.configPath}</dd>
              <dt className="text-base-content/50">Store root</dt>
              <dd className="break-all font-mono text-base-content">{detection.storeRoot}</dd>
            </dl>
          </div>
        ) : (
          <div className="space-y-4">
            {(detection?.reason || detectionError) && (
              <Alert className="alert-soft text-sm" variant="warning">
                <Icon name="warning" className="!text-[18px]" />
                {detection?.reason || detectionError}
              </Alert>
            )}
            <PathField
              label="Altmount Data Directory"
              required
              help="Container path containing Altmount's metadata/ directory and config.yaml. The store root will use this same directory."
              value={basicRoot}
              onChange={(value) => {
                setBasicRoot(value);
                setDetection(null);
                setDetectionError(null);
              }}
            />
            <Button
              variant="outline"
              disabled={!basicRoot.trim()}
              onClick={() => void detectPaths(basicRoot)}
            >
              <Icon name="search" className="!text-[18px]" />
              Detect paths
            </Button>
          </div>
        )}

        <div className="flex items-center gap-3">
          <Button variant="primary" disabled={!canSubmit} onClick={connect}>
            {m.busy === "connect" ? (
              <Spinner className="h-4 w-4" />
            ) : (
              <Icon name="link" className="!text-[18px]" />
            )}
            Connect
          </Button>
          {m.status && m.status.sessionStatus !== "idle" && (
            <span className="text-xs text-base-content/60">
              Connected · {m.categories.length} categor{m.categories.length === 1 ? "y" : "ies"}{" "}
              discovered
            </span>
          )}
        </div>
      </div>
    </Section>
  );
}

// --- Step 2: categories ----------------------------------------------------

function CategoriesStep({ m, onDone }: { m: Hook; onDone: () => void }) {
  const [draft, setDraft] = useState<CategoryMapRow[]>(m.categories);
  const editable = canEditCategoryMappings(m.status?.sessionStatus);
  useEffect(() => setDraft(m.categories), [m.categories]);

  const update = (altmountCategory: string, patch: Partial<CategoryMapRow>) =>
    setDraft((rows) =>
      rows.map((r) => (r.altmountCategory === altmountCategory ? { ...r, ...patch } : r)),
    );

  const save = () =>
    void m
      .saveCategories(
        draft.map((r) => ({
          altmountCategory: r.altmountCategory,
          targetCategory: r.targetCategory ?? null,
          action: r.action,
        })),
      )
      .then((succeeded) => {
        if (succeeded) onDone();
      });

  return (
    <Section
      icon="category"
      title="Map categories"
      subtitle="Choose the InfiniDysk target category for each Altmount category, or exclude it."
    >
      {draft.length === 0 ? (
        <EmptyHint
          icon="category"
          text="No categories discovered yet. Connect with a config.yaml, or scan to discover them."
        />
      ) : (
        <div className="overflow-x-auto">
          <table className="table table-sm">
            <thead>
              <tr>
                <th>Altmount category</th>
                <th>Target InfiniDysk category</th>
                <th>Action</th>
              </tr>
            </thead>
            <tbody>
              {draft.map((r) => (
                <tr key={r.altmountCategory}>
                  <td>
                    <div className="font-mono text-sm">
                      {r.altmountCategory || (
                        <span className="text-base-content/50">(uncategorised)</span>
                      )}
                    </div>
                    {r.altmountType && (
                      <div className="text-[11px] text-base-content/50">{r.altmountType}</div>
                    )}
                  </td>
                  <td>
                    <Input
                      className="input-sm w-full max-w-xs"
                      placeholder="e.g. tv, movies"
                      value={r.targetCategory ?? ""}
                      disabled={!editable || r.action === "exclude"}
                      onChange={(e) =>
                        update(r.altmountCategory, { targetCategory: e.target.value })
                      }
                    />
                  </td>
                  <td>
                    <Select
                      className="select-sm"
                      value={r.action}
                      disabled={!editable}
                      onChange={(e) => update(r.altmountCategory, { action: e.target.value })}
                    >
                      <option value="migrate">Migrate</option>
                      <option value="exclude">Exclude</option>
                    </Select>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <div className="mt-4 flex items-center gap-3">
        <Button variant="primary" disabled={!editable || m.busy === "categories"} onClick={save}>
          {m.busy === "categories" ? (
            <Spinner className="h-4 w-4" />
          ) : (
            <Icon name="save" className="!text-[18px]" />
          )}
          Save mapping
        </Button>
        <span className="text-xs text-base-content/50">
          {editable
            ? "Saving a mapping requires a fresh scan to apply."
            : "Category mappings are locked once migration work starts."}
        </span>
      </div>
    </Section>
  );
}

// --- Step 3: scan ----------------------------------------------------------

function ScanStep({ m, onReview }: { m: Hook; onReview: () => void }) {
  const status = m.status?.sessionStatus;
  const scanning = status === "scanning";
  const scanCancelling = status === "scan_cancelling";
  const conflictingWork = isMigrationWorkActive(status) && !scanning && !scanCancelling;
  const canStart = canStartScanMigration(status);
  const scanned = hasScanData(status);

  return (
    <Section
      icon="search"
      title="Scan the library"
      subtitle="Read every release, triage it, and detect collisions. No network traffic yet."
    >
      {scanCancelling ? (
        <div className="flex items-center gap-3 text-sm text-base-content/70">
          <Spinner className="h-5 w-5" />
          <span>Cancelling scan... waiting for the active filesystem read to drain.</span>
        </div>
      ) : scanning ? (
        <div className="flex items-center gap-3 text-sm text-base-content/70">
          <Spinner className="h-5 w-5" />
          <span>
            Scanning… this reads the metadata tree and decodes each store. It updates automatically.
          </span>
          <Button variant="outline" size="small" onClick={() => void m.cancelScan()}>
            Cancel
          </Button>
        </div>
      ) : (
        <div className="space-y-4">
          {m.summary && scanned && <SummaryTiles summary={m.summary} />}
          <div className="flex flex-wrap items-center gap-3">
            <Button
              variant="primary"
              disabled={m.busy === "scan" || !canStart}
              onClick={() => void m.startScan()}
            >
              {m.busy === "scan" ? (
                <Spinner className="h-4 w-4" />
              ) : (
                <Icon name="search" className="!text-[18px]" />
              )}
              {scanned ? "Re-scan" : "Start scan"}
            </Button>
            {scanned && (
              <Button variant="outline" onClick={onReview}>
                Review results
                <Icon name="arrow_forward" className="!text-[18px]" />
              </Button>
            )}
            {conflictingWork && (
              <span className="text-xs text-base-content/55">
                Wait for the active migration operation to finish before starting a new scan.
              </span>
            )}
          </div>
        </div>
      )}
    </Section>
  );
}

// --- Step 4: review --------------------------------------------------------

function ReviewStep({ m, onRun }: { m: Hook; onRun: () => void }) {
  const [collisions, setCollisions] = useState<CollisionGroup[]>([]);
  const [collisionsLoading, setCollisionsLoading] = useState(true);
  const [collisionLoadError, setCollisionLoadError] = useState<string | null>(null);
  const [confirmRun, setConfirmRun] = useState(false);
  const collisionGeneration = useRef(0);

  const reloadCollisions = useCallback(() => {
    setCollisionsLoading(true);
    void loadTableLatest(
      collisionGeneration,
      m.loadCollisions,
      (groups) => {
        setCollisions(groups);
        setCollisionLoadError(null);
      },
      setCollisionLoadError,
    ).finally(() => setCollisionsLoading(false));
  }, [m.loadCollisions]);
  useEffect(() => reloadCollisions(), [reloadCollisions]);

  const summary = m.summary;
  const canRun = !!summary?.canRun && !collisionsLoading && collisionLoadError === null;
  const needsFreshScan = m.status?.sessionStatus !== "scanned";
  const onlyAlreadyMigrated =
    !!summary && summary.counts.submittable === 0 && summary.counts.alreadyMigrated > 0;

  const doRun = () => {
    setConfirmRun(false);
    void m.startRun().then((succeeded) => {
      if (succeeded) onRun();
    });
  };

  return (
    <div className="flex flex-col gap-6">
      {summary && (
        <Section
          icon="fact_check"
          title="Review"
          subtitle="What will migrate, what it will cost, and what needs a decision."
        >
          <SummaryTiles summary={summary} />
          <div className="mt-3 flex flex-wrap items-center gap-3">
            <Button
              variant="primary"
              disabled={!canRun || m.busy === "run"}
              onClick={() => setConfirmRun(true)}
            >
              <Icon
                name={onlyAlreadyMigrated ? "arrow_forward" : "play_arrow"}
                className="!text-[18px]"
              />
              {onlyAlreadyMigrated ? "Continue to links" : "Start migration"}
            </Button>
            {!canRun &&
              (collisionsLoading ? (
                <span className="text-xs text-base-content/55">Loading collision review…</span>
              ) : collisionLoadError ? (
                <span className="text-xs text-error">
                  Reload the collision review successfully before starting.
                </span>
              ) : needsFreshScan ? (
                <span className="text-xs text-base-content/55">
                  Complete a new scan before starting another migration.
                </span>
              ) : (
                <span className="text-xs text-warning">
                  Resolve blocking collisions and unmapped categories below before running.
                </span>
              ))}
          </div>
        </Section>
      )}

      {collisionLoadError && (
        <Alert className="alert-soft text-sm" variant="danger">
          <Icon name="error" className="!text-[18px]" />
          <span>
            Collision review could not be loaded: {collisionLoadError}. The last successful results
            are retained, and starting is disabled until a refresh succeeds.
          </span>
          <Button
            variant="outline"
            size="small"
            disabled={collisionsLoading}
            onClick={reloadCollisions}
          >
            Retry
          </Button>
        </Alert>
      )}

      <CollisionPanel groups={collisions} />

      <ReleaseGrid m={m} onChanged={reloadCollisions} />

      <ConfirmModal
        show={confirmRun}
        title={onlyAlreadyMigrated ? "Continue without submitting" : "Start migration"}
        message={
          onlyAlreadyMigrated ? (
            <>
              All included releases are already present in InfiniDysk. Nothing will be submitted;
              continue to the optional symlink step using the saved mappings.
            </>
          ) : (
            <>
              This queues {summary?.counts.submittable ?? 0} release(s) into InfiniDysk's download
              pipeline. Your existing InfiniDysk content is untouched. You can pause at any time.
            </>
          )
        }
        cancelText="Cancel"
        confirmText={onlyAlreadyMigrated ? "Continue" : "Start"}
        onCancel={() => setConfirmRun(false)}
        onConfirm={doRun}
      />
    </div>
  );
}

function CollisionPanel({ groups }: { groups: CollisionGroup[] }) {
  if (groups.length === 0) return null;
  const blocking = groups.filter((g) => g.blocking);
  return (
    <Section
      icon="warning"
      title="Collisions"
      subtitle="Releases that would land on the same queue key or mount folder."
    >
      {blocking.length > 0 && (
        <Alert className="alert-soft mb-3 text-sm" variant="danger">
          <Icon name="block" className="!text-[18px]" />
          {blocking.length} blocking collision group(s) — exclude all but one release in each before
          running.
        </Alert>
      )}
      <div className="space-y-3">
        {groups.map((g) => (
          <div
            key={g.key}
            className={`rounded-lg border p-3 ${g.blocking ? "border-error/40" : "border-base-content/10"}`}
          >
            <div className="mb-2 flex items-center gap-2">
              {g.blocking ? (
                <Badge className="badge-sm badge-error">blocking</Badge>
              ) : (
                <Badge className="badge-sm badge-warning badge-soft">warning</Badge>
              )}
              <span className="font-mono text-xs text-base-content/70">{g.key}</span>
            </div>
            <ul className="space-y-1">
              {g.members.map((mem) => (
                <li key={mem.storeRef} className="flex flex-wrap items-center gap-2 text-xs">
                  <VerdictBadge verdict={mem.verdict} />
                  <span className="font-mono">{mem.submitFileName}</span>
                  <ReasonBadges reasons={mem.reasons} />
                </li>
              ))}
            </ul>
          </div>
        ))}
      </div>
    </Section>
  );
}

function ReleaseGrid({ m, onChanged }: { m: Hook; onChanged: () => void }) {
  const { loadReleases } = m;
  const [filters, setFilters] = useState<ReleaseFilters>({
    page: 1,
    pageSize: 50,
    verdict: "",
    included: "",
    q: "",
    sort: "",
  });
  const [searchDraft, setSearchDraft] = useState("");
  const debouncedSearch = useDebouncedValue(searchDraft, 300);
  const [rows, setRows] = useState<ReleaseRow[]>([]);
  const [total, setTotal] = useState(0);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const editable = canEditReleaseSelection(m.status?.sessionStatus);
  const loadGeneration = useRef(0);

  useEffect(() => {
    setFilters((current) =>
      current.q === debouncedSearch ? current : { ...current, q: debouncedSearch, page: 1 },
    );
  }, [debouncedSearch]);

  const load = useCallback(
    async (f: ReleaseFilters) => {
      setLoading(true);
      try {
        await loadTableLatest(
          loadGeneration,
          () => loadReleases(f),
          (data) => {
            setRows(data.releases);
            setTotal(data.total);
            setLoadError(null);
          },
          setLoadError,
        );
      } finally {
        setLoading(false);
      }
    },
    [loadReleases],
  );

  useEffect(() => {
    void load(filters);
  }, [load, filters]);

  const toggleInclude = (row: ReleaseRow) =>
    void m.setInclude([row.storeRef], !row.included).then((succeeded) => {
      if (!succeeded) return;
      void load(filters);
      onChanged();
    });

  const pages = Math.max(1, Math.ceil(total / filters.pageSize));

  return (
    <Section icon="list" title="Releases" subtitle={`${total} release(s)`}>
      {loadError && (
        <Alert className="alert-soft mb-3 text-sm" variant="danger">
          <Icon name="error" className="!text-[18px]" />
          Release data could not be loaded: {loadError}. The last successful results are shown when
          available.
        </Alert>
      )}

      <div className="mb-3 flex flex-wrap items-center gap-2">
        <Select
          className="select-sm"
          value={filters.verdict}
          onChange={(e) => setFilters({ ...filters, verdict: e.target.value, page: 1 })}
        >
          <option value="">All verdicts</option>
          <option value="green">Green</option>
          <option value="amber">Amber</option>
          <option value="red">Red</option>
        </Select>
        <Select
          className="select-sm"
          value={filters.included}
          onChange={(e) => setFilters({ ...filters, included: e.target.value, page: 1 })}
        >
          <option value="">Included &amp; excluded</option>
          <option value="true">Included only</option>
          <option value="false">Excluded only</option>
        </Select>
        <Select
          className="select-sm"
          value={filters.sort}
          onChange={(e) => setFilters({ ...filters, sort: e.target.value })}
        >
          <option value="">Migrating first</option>
          <option value="bytes">Largest first</option>
          <option value="-bytes">Smallest first</option>
          <option value="name">Name A–Z</option>
          <option value="-name">Name Z–A</option>
        </Select>
        <Input
          className="input-sm w-48"
          placeholder="Search name…"
          value={searchDraft}
          onChange={(e) => setSearchDraft(e.target.value)}
        />
        <Button variant="ghost" size="small" onClick={() => void load(filters)}>
          <Icon name="refresh" className="!text-[16px]" />
        </Button>
      </div>

      <div className="overflow-x-auto">
        <table className="table table-sm">
          <thead>
            <tr>
              <th>Include</th>
              <th>Release</th>
              <th>Verdict</th>
              <th>Category</th>
              <th className="text-right">Est. fetch</th>
              <th>Flags</th>
            </tr>
          </thead>
          <tbody>
            {loading && rows.length === 0 ? (
              <tr>
                <td colSpan={6}>
                  <div className="flex justify-center py-6">
                    <Spinner className="h-5 w-5" />
                  </div>
                </td>
              </tr>
            ) : loadError && rows.length === 0 ? (
              <tr>
                <td colSpan={6}>
                  <div className="py-6 text-center text-sm text-error">
                    Release data could not be loaded.
                  </div>
                </td>
              </tr>
            ) : rows.length === 0 ? (
              <tr>
                <td colSpan={6}>
                  <div className="py-6 text-center text-sm text-base-content/50">
                    No releases match.
                  </div>
                </td>
              </tr>
            ) : (
              rows.map((r) => (
                <tr key={r.storeRef}>
                  <td>
                    <input
                      type="checkbox"
                      className="checkbox checkbox-sm checkbox-primary"
                      checked={r.included}
                      disabled={!editable || m.busy === "include"}
                      onChange={() => toggleInclude(r)}
                    />
                  </td>
                  <td className="max-w-xs">
                    <div className="truncate font-mono text-xs" title={r.submitFileName}>
                      {r.submitFileName}
                    </div>
                    <div className="text-[11px] text-base-content/45">
                      {r.metaFileCount} file(s){r.jobNameDiverges ? " · job name diverges" : ""}
                    </div>
                  </td>
                  <td>
                    {r.verdict ? (
                      <VerdictBadge verdict={r.verdict} />
                    ) : (
                      <span className="text-base-content/40">&mdash;</span>
                    )}
                  </td>
                  <td className="text-xs">
                    <span className="font-mono">{r.altmountCategory || "—"}</span>
                    {r.targetCategory && (
                      <>
                        <Icon
                          name="arrow_forward"
                          className="!text-[12px] mx-1 align-middle text-base-content/40"
                        />
                        <span className="font-mono">{r.targetCategory}</span>
                      </>
                    )}
                  </td>
                  <td className="text-right font-mono text-xs">
                    {formatBytes(r.estFetchBytesLazy)}
                  </td>
                  <td>
                    <ReasonBadges reasons={r.verdictReasons} />
                  </td>
                </tr>
              ))
            )}
          </tbody>
        </table>
      </div>

      {pages > 1 && (
        <div className="mt-3 flex items-center justify-center gap-2">
          <Button
            variant="ghost"
            size="small"
            disabled={filters.page <= 1}
            onClick={() => setFilters({ ...filters, page: filters.page - 1 })}
          >
            <Icon name="chevron_left" className="!text-[16px]" />
          </Button>
          <span className="text-xs text-base-content/60">
            Page {filters.page} / {pages}
          </span>
          <Button
            variant="ghost"
            size="small"
            disabled={filters.page >= pages}
            onClick={() => setFilters({ ...filters, page: filters.page + 1 })}
          >
            <Icon name="chevron_right" className="!text-[16px]" />
          </Button>
        </div>
      )}
    </Section>
  );
}

// --- Step 5: run -----------------------------------------------------------

function RunStep({ m, onLinks }: { m: Hook; onLinks: () => void }) {
  const status = m.status?.sessionStatus;
  const subs = m.status?.submissions ?? {};
  const terminal =
    (subs["completed"] ?? 0) +
    (subs["history_cleared"] ?? 0) +
    (subs["failed"] ?? 0) +
    (subs["evicted"] ?? 0) +
    (subs["skipped"] ?? 0);
  const inFlight =
    (subs["pending"] ?? 0) +
    (subs["submitting"] ?? 0) +
    (subs["submitted"] ?? 0) +
    (subs["processing"] ?? 0);
  const complete = status === "complete";
  const cancelled = status === "cancelled";
  const cancelling = status === "cancelling";
  const runFinished = cancelled || canLinkStep(status);
  const submissionIssues = m.status?.submissionIssues ?? [];
  const [confirmCancel, setConfirmCancel] = useState(false);

  return (
    <Section
      icon={
        complete
          ? "check_circle"
          : cancelled
            ? "cancel"
            : cancelling
              ? "progress_activity"
              : "rocket_launch"
      }
      title={
        complete
          ? "Migration complete"
          : cancelled
            ? "Migration cancelled"
            : cancelling
              ? "Cancelling migration"
              : status === "paused"
                ? "Migration paused"
                : "Migration running"
      }
      subtitle={
        complete
          ? "Every release reached a terminal state."
          : cancelled
            ? "Complete a new scan before starting another migration."
            : cancelling
              ? "Waiting for the current queue submission to drain and be reconciled."
              : "Releases are submitted up to the queue-depth gate and reconciled as they import."
      }
    >
      <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
        <StatTile label="Pending" value={subs["pending"] ?? 0} />
        <StatTile label="Submitting" value={subs["submitting"] ?? 0} />
        <StatTile label="Submitted" value={subs["submitted"] ?? 0} />
        <StatTile label="Processing" value={subs["processing"] ?? 0} />
        <StatTile
          label="Imported"
          value={(subs["completed"] ?? 0) + (subs["history_cleared"] ?? 0)}
          tone="success"
        />
        <StatTile
          label="Failed"
          value={subs["failed"] ?? 0}
          tone={(subs["failed"] ?? 0) > 0 ? "error" : undefined}
        />
        <StatTile
          label="Evicted"
          value={subs["evicted"] ?? 0}
          tone={(subs["evicted"] ?? 0) > 0 ? "warning" : undefined}
        />
        <StatTile label="In flight" value={inFlight} />
        <StatTile label="Terminal" value={terminal} />
      </div>

      {runFinished && submissionIssues.length > 0 && (
        <SubmissionIssueList issues={submissionIssues} />
      )}

      <div className="mt-4 flex flex-wrap items-center gap-3">
        {status === "running" && (
          <Button variant="warning" disabled={m.busy === "run"} onClick={() => void m.pauseRun()}>
            <Icon name="pause" className="!text-[18px]" /> Pause
          </Button>
        )}
        {status === "paused" && (
          <Button variant="primary" disabled={m.busy === "run"} onClick={() => void m.resumeRun()}>
            <Icon name="play_arrow" className="!text-[18px]" /> Resume
          </Button>
        )}
        {(status === "running" || status === "paused") && (
          <Button
            variant="outline"
            disabled={m.busy === "run"}
            onClick={() => setConfirmCancel(true)}
          >
            <Icon name="stop" className="!text-[18px]" /> Cancel
          </Button>
        )}
        {(status === "running" || cancelling) && (
          <span className="text-xs text-base-content/50">Live-updating…</span>
        )}
      </div>

      {complete && (
        <div className="mt-4 flex flex-wrap items-center gap-3 border-t border-base-content/10 pt-4">
          <Button variant="outline" onClick={onLinks}>
            <Icon name="link" className="!text-[18px]" />
            Rewrite library symlinks
          </Button>
          <span className="text-xs text-base-content/50">
            Optional — repoint Sonarr/Radarr/Plex symlinks at InfiniDysk so nothing needs
            re-importing.
          </span>
        </div>
      )}
      {complete && <HistoryCleanupAction m={m} />}

      <ConfirmModal
        show={confirmCancel}
        title="Cancel migration"
        message={<>Cancelling requires a new scan before another run. Cancel the migration?</>}
        cancelText="Keep running"
        confirmText="Cancel migration"
        onCancel={() => setConfirmCancel(false)}
        onConfirm={() => {
          setConfirmCancel(false);
          void m.cancelRun();
        }}
      />
    </Section>
  );
}

function SubmissionIssueList({ issues }: { issues: SubmissionIssue[] }) {
  return (
    <div className="mt-4 overflow-hidden rounded-lg border border-base-content/10 bg-base-200/20">
      <div className="flex items-center gap-2 border-b border-base-content/10 px-3 py-2">
        <Icon name="report" className="!text-[18px] text-warning" />
        <h3 className="text-sm font-medium">Failed or evicted releases</h3>
        <Badge className="badge-sm badge-ghost font-mono">{issues.length}</Badge>
      </div>
      <div className="max-h-80 overflow-auto">
        <table className="table table-sm">
          <thead>
            <tr>
              <th>Status</th>
              <th>Release</th>
              <th>Reason</th>
            </tr>
          </thead>
          <tbody>
            {issues.map((issue) => (
              <tr key={`${issue.state}:${issue.storeRef}`}>
                <td>
                  <Badge
                    className={`badge-sm badge-soft ${issue.state === "failed" ? "badge-error" : "badge-warning"}`}
                  >
                    {issue.state === "failed" ? "Failed" : "Evicted"}
                  </Badge>
                </td>
                <td className="max-w-xs">
                  <div className="truncate font-mono text-xs" title={issue.submitFileName}>
                    {issue.submitFileName}
                  </div>
                </td>
                <td className="min-w-64 text-xs text-base-content/70">{issue.reason}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

// --- Step 6: optional symlink continuity -----------------------------------

function SymlinkStep({ m }: { m: Hook }) {
  const status = m.status?.sessionStatus;
  const [form, setForm] = useState<SymlinkPlanForm>({
    libraryRoot: m.status?.symlinks?.symlinkLibraryRoot ?? "",
    backupDir: m.status?.symlinks?.symlinkBackupDir ?? m.status?.symlinks?.defaultBackupDir ?? "",
  });

  // Sync from the session once it loads (without clobbering user edits).
  useEffect(() => {
    if (!m.status) return;
    setForm((f) => ({
      libraryRoot: f.libraryRoot || (m.status?.symlinks?.symlinkLibraryRoot ?? ""),
      backupDir:
        f.backupDir ||
        (m.status?.symlinks?.symlinkBackupDir ?? "") ||
        (m.status?.symlinks?.defaultBackupDir ?? ""),
    }));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [m.status?.sessionStatus]);

  const linking = status === "linking";
  const applying = status === "applying";
  const removingOrphans = status === "removing_orphans";
  const restoring = status === "restoring";
  const linked = status === "linked";
  const busyPlan = m.busy === "symlink-plan";
  const step6Active = linking || applying || removingOrphans || restoring || m.busy !== null;
  const canPlan = form.libraryRoot.trim().length > 0 && !step6Active;

  return (
    <div className="flex flex-col gap-6">
      <Section
        icon="link"
        title="Rewrite library symlinks (optional)"
        subtitle="Repoint your Sonarr/Radarr/Plex symlinks from Altmount to InfiniDysk so the migrated content is used with no re-import."
      >
        <Alert className="alert-soft mb-4 text-sm" variant="info">
          <Icon name="info" className="!text-[18px]" />
          This is the only step that changes your media library. A restore tarball is written before
          any rewrite or optional orphan removal, and real files and symlink targets are never
          touched.
        </Alert>

        <div className="space-y-4">
          <PathField
            label="Library Root"
            required
            help="Root of the arr/Plex library whose symlinks currently point at Altmount. Needed only for in-container Apply; download the shell script to rewrite on the host instead."
            value={form.libraryRoot}
            disabled={step6Active}
            onChange={(v) => setForm({ ...form, libraryRoot: v })}
          />
          <PathField
            label="Backup Directory"
            help="Defaults to /config/migration-backups — no extra volume needed. Leave blank to use that default."
            value={form.backupDir}
            disabled={step6Active}
            onChange={(v) => setForm({ ...form, backupDir: v })}
          />

          <div className="flex flex-wrap items-center gap-3">
            <Button variant="primary" disabled={!canPlan} onClick={() => void m.planSymlinks(form)}>
              {busyPlan || linking ? (
                <Spinner className="h-4 w-4" />
              ) : (
                <Icon name="search" className="!text-[18px]" />
              )}
              {linked ? "Rebuild plan" : "Build plan"}
            </Button>
            {(linking || applying || removingOrphans) && (
              <Button
                variant="outline"
                size="small"
                disabled={m.busy === "symlink-cancel"}
                onClick={() => void m.cancelSymlinkOperation()}
              >
                {m.busy === "symlink-cancel" ? (
                  <Spinner className="h-4 w-4" />
                ) : (
                  <Icon name="stop" className="!text-[18px]" />
                )}
                Cancel
              </Button>
            )}
            {linking && (
              <span className="text-xs text-base-content/60">
                Scanning the library and matching symlinks… updates automatically.
              </span>
            )}
            {applying && (
              <span className="flex items-center gap-2 text-xs text-base-content/60">
                <Spinner className="h-4 w-4" /> Applying rewrites…
              </span>
            )}
            {removingOrphans && (
              <span className="flex items-center gap-2 text-xs text-base-content/60">
                <Spinner className="h-4 w-4" /> Removing orphaned Altmount symlinks…
              </span>
            )}
            {restoring && (
              <span className="flex items-center gap-2 text-xs text-base-content/60">
                <Spinner className="h-4 w-4" /> Restoring symlinks…
              </span>
            )}
          </div>
        </div>
      </Section>

      {linked && <SymlinkResults m={m} />}
    </div>
  );
}

function SymlinkResults({ m }: { m: Hook }) {
  const { loadSymlinks } = m;
  const [filters, setFilters] = useState<SymlinkFilters>({
    page: 1,
    pageSize: 100,
    status: "rewrite",
    q: "",
    sort: "",
  });
  const [searchDraft, setSearchDraft] = useState("");
  const debouncedSearch = useDebouncedValue(searchDraft, 300);
  const [data, setData] = useState<{
    total: number;
    counts: Record<string, number>;
    rows: SymlinkRow[];
  }>({ total: 0, counts: {}, rows: [] });
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [confirmApply, setConfirmApply] = useState(false);
  const [confirmOrphanRemoval, setConfirmOrphanRemoval] = useState(false);
  const loadGeneration = useRef(0);

  useEffect(() => {
    setFilters((current) =>
      current.q === debouncedSearch ? current : { ...current, q: debouncedSearch, page: 1 },
    );
  }, [debouncedSearch]);

  const load = useCallback(
    async (f: SymlinkFilters) => {
      setLoading(true);
      try {
        await loadTableLatest(
          loadGeneration,
          () => loadSymlinks(f),
          (res) => {
            setData({ total: res.total, counts: res.counts, rows: res.rows });
            setLoadError(null);
          },
          setLoadError,
        );
      } finally {
        setLoading(false);
      }
    },
    [loadSymlinks],
  );

  useEffect(() => {
    void load(filters);
  }, [load, filters]);

  const counts = data.counts;
  const rewrites = counts["rewrite"] ?? 0;
  const orphans = counts["orphan"] ?? 0;
  const unreadable = counts["unreadable"] ?? 0;
  const applied = counts["applied"] ?? 0;
  const removed = counts["removed"] ?? 0;
  const failed = counts["failed"] ?? 0;
  const canApply = !loading && loadError === null && rewrites > 0 && m.busy === null;
  const canRemoveOrphans = !loading && loadError === null && orphans > 0 && m.busy === null;
  const pages = Math.max(1, Math.ceil(data.total / filters.pageSize));

  const doApply = (acknowledgeUnreadable?: boolean) => {
    setConfirmApply(false);
    void m.applySymlinks(acknowledgeUnreadable === true);
  };

  const doRemoveOrphans = () => {
    setConfirmOrphanRemoval(false);
    void m.removeOrphanSymlinks();
  };

  return (
    <Section
      icon="rule"
      title="Symlink plan"
      subtitle="Review first. Rewrites and optional orphan cleanup are separate confirmed actions."
    >
      {loadError && (
        <Alert className="alert-soft mb-4 text-sm" variant="danger">
          <Icon name="error" className="!text-[18px]" />
          Symlink data could not be loaded: {loadError}. The last successful results are retained,
          and applying rewrites is disabled until a refresh succeeds.
        </Alert>
      )}

      <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-4 xl:grid-cols-8">
        <StatTile
          label="Rewrite"
          value={rewrites}
          tone="success"
          help={SYMLINK_STATUS_HELP["rewrite"]}
        />
        <StatTile
          label="Orphan"
          value={orphans}
          tone={orphans > 0 ? "warning" : undefined}
          help={SYMLINK_STATUS_HELP["orphan"]}
        />
        {unreadable > 0 && (
          <StatTile
            label="Unreadable"
            value={unreadable}
            tone="error"
            help={SYMLINK_STATUS_HELP["unreadable"]}
          />
        )}
        <StatTile
          label="InfiniDysk"
          value={counts["already-nzbdav"] ?? 0}
          help={SYMLINK_STATUS_HELP["already-nzbdav"]}
        />
        <StatTile
          label="Other"
          value={counts["not-altmount"] ?? 0}
          help={SYMLINK_STATUS_HELP["not-altmount"]}
        />
        <StatTile
          label="Applied"
          value={applied}
          tone={applied > 0 ? "success" : undefined}
          help={SYMLINK_STATUS_HELP["applied"]}
        />
        <StatTile
          label="Removed"
          value={removed}
          tone={removed > 0 ? "warning" : undefined}
          help={SYMLINK_STATUS_HELP["removed"]}
        />
        <StatTile
          label="Failed"
          value={failed}
          tone={failed > 0 ? "error" : undefined}
          help={SYMLINK_STATUS_HELP["failed"]}
        />
      </div>

      <div className="mt-4 flex flex-wrap items-center gap-3">
        <Button variant="primary" disabled={!canApply} onClick={() => setConfirmApply(true)}>
          {m.busy === "symlink-apply" ? (
            <Spinner className="h-4 w-4" />
          ) : (
            <Icon name="published_with_changes" className="!text-[18px]" />
          )}
          Apply {rewrites} rewrite(s)
        </Button>
        <Button
          variant="danger"
          disabled={!canRemoveOrphans}
          onClick={() => setConfirmOrphanRemoval(true)}
        >
          {m.busy === "symlink-orphan-remove" ? (
            <Spinner className="h-4 w-4" />
          ) : (
            <Icon name="link_off" className="!text-[18px]" />
          )}
          Remove {orphans} orphaned link(s)
        </Button>
        {!loading &&
          !loadError &&
          rewrites === 0 &&
          applied === 0 &&
          (unreadable > 0 ? (
            <span className="text-xs text-error">
              No verified rewrites are available; {unreadable} unreadable symlink(s) remain
              unchanged and require review.
            </span>
          ) : (
            <span className="text-xs text-base-content/50">
              No rewrites to apply — every symlink is already correct, orphaned, or unrelated.
            </span>
          ))}
        {applied > 0 && (
          <span className="text-xs text-success">
            {applied} symlink(s) rewritten. A restore tarball is in your backup directory.
            {unreadable > 0 && (
              <span className="ml-1 text-error">
                {unreadable} unreadable symlink(s) remain unchanged.
              </span>
            )}
          </span>
        )}
        {removed > 0 && (
          <span className="text-xs text-warning">
            {removed} orphaned symlink(s) removed. Use the restore archive if you need to recreate
            them.
          </span>
        )}
      </div>

      <SymlinkRestoreAction m={m} onRestored={() => void load(filters)} />

      {!loading && !loadError && rewrites === 0 && <HistoryCleanupAction m={m} />}

      <div className="mt-4 mb-3 flex flex-wrap items-center gap-2">
        <Select
          className="select-sm"
          value={filters.status}
          onChange={(e) => setFilters({ ...filters, status: e.target.value, page: 1 })}
        >
          <option value="">All statuses</option>
          <option value="rewrite">Rewrite</option>
          <option value="orphan">Orphan</option>
          <option value="unreadable">Unreadable</option>
          <option value="already-nzbdav">InfiniDysk</option>
          <option value="not-altmount">Other</option>
          <option value="applied">Applied</option>
          <option value="failed">Failed</option>
          <option value="removed">Removed</option>
        </Select>
        <Input
          className="input-sm w-56"
          placeholder="Search path…"
          value={searchDraft}
          onChange={(e) => setSearchDraft(e.target.value)}
        />
        <Button variant="ghost" size="small" onClick={() => void load(filters)}>
          <Icon name="refresh" className="!text-[16px]" />
        </Button>
        <a className="btn btn-sm btn-ghost" href={m.symlinkCsvHref(filters)} download>
          <Icon name="download" className="!text-[16px]" /> CSV
        </a>
        <a
          className="btn btn-sm btn-ghost"
          href={m.symlinkShellHref(filters)}
          download
          title="Host-runnable rewrite script. Does not update the wizard table; prefer in-container Apply when the library is mounted."
        >
          <Icon name="terminal" className="!text-[16px]" /> Shell script
        </a>
      </div>

      <div className="overflow-x-auto">
        <table className="table table-sm">
          <thead>
            <tr>
              <th>Status</th>
              <th>Symlink</th>
              <th>Target</th>
              <th>Match</th>
            </tr>
          </thead>
          <tbody>
            {loading && data.rows.length === 0 ? (
              <tr>
                <td colSpan={4}>
                  <div className="flex justify-center py-6">
                    <Spinner className="h-5 w-5" />
                  </div>
                </td>
              </tr>
            ) : loadError && data.rows.length === 0 ? (
              <tr>
                <td colSpan={4}>
                  <div className="py-6 text-center text-sm text-error">
                    Symlink data could not be loaded.
                  </div>
                </td>
              </tr>
            ) : data.rows.length === 0 ? (
              <tr>
                <td colSpan={4}>
                  <div className="py-6 text-center text-sm text-base-content/50">
                    No symlinks match.
                  </div>
                </td>
              </tr>
            ) : (
              data.rows.map((r) => (
                <tr key={r.id}>
                  <td>
                    <SymlinkStatusBadge status={r.status} />
                  </td>
                  <td className="max-w-xs">
                    <div className="truncate font-mono text-xs" title={r.symlinkPath}>
                      {r.symlinkPath}
                    </div>
                  </td>
                  <td className="max-w-md">
                    <div
                      className="truncate font-mono text-[11px] text-base-content/50"
                      title={r.oldTarget}
                    >
                      {r.oldTarget}
                    </div>
                    {r.newTarget && (
                      <div
                        className="truncate font-mono text-[11px] text-success"
                        title={r.newTarget}
                      >
                        → {r.newTarget}
                      </div>
                    )}
                    {r.error && (
                      <div className="truncate text-[11px] text-error" title={r.error}>
                        {r.error}
                      </div>
                    )}
                  </td>
                  <td className="text-[11px] text-base-content/60">
                    <MatchMethodLabel method={r.matchMethod} />
                  </td>
                </tr>
              ))
            )}
          </tbody>
        </table>
      </div>

      {pages > 1 && (
        <div className="mt-3 flex items-center justify-center gap-2">
          <Button
            variant="ghost"
            size="small"
            disabled={filters.page <= 1}
            onClick={() => setFilters({ ...filters, page: filters.page - 1 })}
          >
            <Icon name="chevron_left" className="!text-[16px]" />
          </Button>
          <span className="text-xs text-base-content/60">
            Page {filters.page} / {pages}
          </span>
          <Button
            variant="ghost"
            size="small"
            disabled={filters.page >= pages}
            onClick={() => setFilters({ ...filters, page: filters.page + 1 })}
          >
            <Icon name="chevron_right" className="!text-[16px]" />
          </Button>
        </div>
      )}

      <ConfirmModal
        show={confirmApply}
        title="Apply symlink rewrites"
        message={
          <>
            This repoints {rewrites} symlink(s) from Altmount to InfiniDysk. A restore tarball is
            written first, and only symlinks are changed — never the files they point at. Continue?
          </>
        }
        {...(unreadable > 0
          ? {
              checkboxMessage: `I acknowledge that ${unreadable} unreadable symlink(s) will remain unchanged`,
            }
          : {})}
        requireCheckbox={unreadable > 0}
        {...(unreadable > 0
          ? {
              errorMessage: `${unreadable} symlink(s) could not be classified and may still point at Altmount.`,
            }
          : {})}
        cancelText="Cancel"
        confirmText="Apply"
        onCancel={() => setConfirmApply(false)}
        onConfirm={doApply}
      />

      <ConfirmModal
        show={confirmOrphanRemoval}
        title="Remove orphaned Altmount symlinks"
        message={
          <>
            This deletes {orphans} symlink entry(s) from your media library, so those paths will
            appear missing to Sonarr, Radarr, Plex, and other applications. It does not delete files
            stored by Altmount or InfiniDysk, and it does not tell your Arr applications to search
            or re-grab them. A verified restore archive is written first, and only links still
            pointing to the Altmount target recorded by this plan are removed. Real files, changed
            links, unreadable links, and target data remain untouched.
            <span className="mt-2 block font-semibold">
              After removal, run a Refresh &amp; Scan job in each affected Arr application so it
              detects the deleted links and marks those files as missing before you initiate or
              schedule re-grabs.
            </span>
          </>
        }
        checkboxMessage="I understand these library paths will remain missing until my Arr applications re-grab them or I restore the backup"
        requireCheckbox
        errorMessage="This cleanup is optional. Cancel if you need to keep Altmount serving any unmatched files."
        cancelText="Cancel"
        confirmText="Remove orphaned links"
        onCancel={() => setConfirmOrphanRemoval(false)}
        onConfirm={doRemoveOrphans}
      />
    </Section>
  );
}

function SymlinkRestoreAction({ m, onRestored }: { m: Hook; onRestored: () => void }) {
  const { loadSymlinkBackups, setError } = m;
  const [backups, setBackups] = useState<SymlinkBackupInfo[]>([]);
  const [loading, setLoading] = useState(true);
  const [selected, setSelected] = useState("");
  const [confirm, setConfirm] = useState(false);
  const busy = m.busy === "symlink-restore";
  const step6Busy = m.busy !== null;
  const archive = backups.find((b) => b.fileName === selected);
  const result = m.symlinkRestoreResult;
  const loadGeneration = useRef(0);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      await loadTableLatest(
        loadGeneration,
        loadSymlinkBackups,
        (items) => {
          setBackups(items);
          setSelected((current) =>
            items.some((item) => item.fileName === current)
              ? current
              : (items.find((item) => item.isValid)?.fileName ?? items[0]?.fileName ?? ""),
          );
        },
        (message) => setError(message),
      );
    } finally {
      setLoading(false);
    }
  }, [loadSymlinkBackups, setError]);

  useEffect(() => {
    void load();
  }, [load]);

  const restore = () => {
    if (!selected) return;
    setConfirm(false);
    void m.restoreSymlinks(selected).then((succeeded) => {
      if (!succeeded) return;
      onRestored();
      void load();
    });
  };

  return (
    <div className="mt-4 rounded-box border border-base-content/10 bg-base-200/40 p-4">
      <div className="flex items-start gap-3">
        <Icon name="restore" className="mt-0.5 !text-[20px] text-base-content/60" />
        <div className="min-w-0 flex-1">
          <h3 className="text-sm font-semibold">Restore Symlinks</h3>
          <p className="mt-1 text-xs text-base-content/60">
            Roll back a previous rewrite or recreate links removed by orphan cleanup. Real files and
            changed links are left untouched.
          </p>

          <div className="mt-3 flex flex-wrap items-center gap-2">
            <Select
              className="select-sm min-w-64 max-w-full"
              value={selected}
              disabled={loading || backups.length === 0 || step6Busy}
              onChange={(e) => setSelected(e.target.value)}
            >
              {backups.length === 0 && <option value="">No restore archives found</option>}
              {backups.map((backup) => (
                <option key={backup.fileName} value={backup.fileName} disabled={!backup.isValid}>
                  {backup.kind === "orphan-removal" ? "Orphan removal" : "Rewrite"} —{" "}
                  {new Date(backup.createdAt).toLocaleString()} — {backup.entryCount} link(s)
                  {backup.isValid ? "" : " — unreadable"}
                </option>
              ))}
            </Select>
            <Button
              variant="outline"
              size="small"
              disabled={!archive?.isValid || step6Busy}
              onClick={() => setConfirm(true)}
            >
              {busy ? (
                <Spinner className="h-4 w-4" />
              ) : (
                <Icon name="restore" className="!text-[18px]" />
              )}
              Restore
            </Button>
            <Button
              variant="ghost"
              size="small"
              disabled={loading || step6Busy}
              onClick={() => void load()}
            >
              <Icon name="refresh" className="!text-[16px]" />
            </Button>
          </div>

          {archive && (
            <div className="mt-2 text-[11px] text-base-content/50">
              <span className="font-mono">{archive.fileName}</span> ·{" "}
              {formatBytes(archive.sizeBytes)}
              <span className="ml-2">
                {archive.kind === "orphan-removal" ? "orphan-removal backup" : "rewrite backup"}
              </span>
              {archive.legacyEntryCount > 0 && (
                <span className="ml-2 text-warning">
                  {archive.legacyEntryCount} older-format link(s) require the current rewrite plan
                  for verification.
                </span>
              )}
              {!archive.isValid && <span className="ml-2 text-error">{archive.error}</span>}
            </div>
          )}

          {result && result.fileName === selected && (
            <Alert
              className="alert-soft mt-3 text-xs"
              variant={result.failed > 0 ? "warning" : "success"}
            >
              <Icon
                name={result.failed > 0 ? "warning" : "check_circle"}
                className="!text-[18px]"
              />
              <div>
                Restored {result.restored}; already restored {result.alreadyRestored}; failed{" "}
                {result.failed}.
                {result.requeued > 0 && ` ${result.requeued} link(s) are ready to rewrite again.`}
                {result.orphansRestored > 0 &&
                  ` ${result.orphansRestored} orphaned link(s) were returned to the plan.`}
                {result.issues.length > 0 && (
                  <ul className="mt-2 list-disc space-y-1 pl-4">
                    {result.issues.map((issue, index) => (
                      <li key={`${issue.path}-${index}`}>
                        <span className="font-mono">{issue.path}</span>: {issue.reason}
                      </li>
                    ))}
                  </ul>
                )}
              </div>
            </Alert>
          )}
        </div>
      </div>

      <ConfirmModal
        show={confirm}
        title="Restore symlinks"
        message={
          archive?.kind === "orphan-removal" ? (
            <>
              Recreate {archive.entryCount} symlink(s) removed by orphan cleanup from{" "}
              <span className="font-mono">{archive.fileName}</span>? Only absent paths are
              recreated. Real files, directories, and differently targeted links are never
              overwritten.
            </>
          ) : (
            <>
              Restore {archive?.entryCount ?? 0} symlink(s) from{" "}
              <span className="font-mono">{archive?.fileName}</span>? Links still pointing at their
              recorded InfiniDysk targets are restored, and missing links can be recreated. Real
              files, link targets, and links changed after the rewrite are never overwritten.
            </>
          )
        }
        cancelText="Cancel"
        confirmText="Restore"
        onCancel={() => setConfirm(false)}
        onConfirm={restore}
      />
    </div>
  );
}

function HistoryCleanupAction({ m }: { m: Hook }) {
  const [confirm, setConfirm] = useState(false);
  const cleanup = m.status?.historyCleanup;
  const eligible = cleanup?.eligible ?? 0;
  const cleared = cleanup?.cleared ?? 0;
  const pending = cleanup?.pending ?? 0;
  const busy = m.busy === "history-cleanup";
  const result = m.historyCleanupResult;

  const runCleanup = () => {
    setConfirm(false);
    void m.cleanupHistory();
  };

  return (
    <div className="mt-4 flex flex-wrap items-center gap-3 border-t border-base-content/10 pt-4">
      <Button variant="ghost" disabled={busy || pending === 0} onClick={() => setConfirm(true)}>
        {busy ? (
          <Spinner className="h-4 w-4" />
        ) : (
          <Icon name="delete_sweep" className="!text-[18px]" />
        )}
        Clear migration history
      </Button>
      <span className="text-xs text-base-content/50">
        {eligible === 0
          ? "No completed migration history is eligible for cleanup."
          : pending === 0
            ? `Migration history cleanup recorded for ${cleared} release(s).`
            : `${pending} completed migration history item(s) can be removed without deleting mounted content.`}
      </span>
      {result && (
        <span className="text-xs text-success">
          Removed {result.removed}; already absent {result.alreadyAbsent}; skipped {result.skipped}.
        </span>
      )}
      <ConfirmModal
        show={confirm}
        title="Clear migration history"
        message={
          <>
            This removes {pending} completed migration item(s) from SAB history. Migrated InfiniDysk
            files remain mounted and are never deleted. Continue?
          </>
        }
        cancelText="Cancel"
        confirmText="Clear history"
        onCancel={() => setConfirm(false)}
        onConfirm={runCleanup}
      />
    </div>
  );
}

function SymlinkStatusBadge({ status }: { status: string }) {
  const cls =
    status === "rewrite"
      ? "badge-info"
      : status === "applied"
        ? "badge-success"
        : status === "failed" || status === "unreadable"
          ? "badge-error"
          : status === "removed"
            ? "badge-warning"
            : status === "orphan"
              ? "badge-warning"
              : "badge-ghost";
  const badge = (
    <Badge className={`badge-sm ${cls} badge-soft cursor-help`}>
      {SYMLINK_STATUS_LABELS[status] ?? status}
    </Badge>
  );
  const help = SYMLINK_STATUS_HELP[status];
  return help ? <Tooltip content={help}>{badge}</Tooltip> : badge;
}

function MatchMethodLabel({ method }: { method?: string | null | undefined }) {
  if (!method) return <>&mdash;</>;
  const presentation = MATCH_METHODS[method];
  if (!presentation) return <>{method}</>;
  return (
    <Tooltip content={presentation.help}>
      <span className="cursor-help underline decoration-dotted underline-offset-2">
        {presentation.label}
      </span>
    </Tooltip>
  );
}

// --- shared bits -----------------------------------------------------------

function ResetFooter({ m }: { m: Hook }) {
  const [confirmReset, setConfirmReset] = useState(false);
  const [manage, setManage] = useState(false);
  const [confirmForget, setConfirmForget] = useState(false);
  const resetActive = isMigrationWorkActive(m.status?.sessionStatus);
  const resetDisabled = !canResetMigration(m.status?.sessionStatus, m.busy);

  const openManage = () => {
    setManage(true);
    void m.loadMigrationData();
  };

  return (
    <div className="border-t border-base-content/10 pt-4">
      <div className="flex flex-wrap items-center gap-2">
        <Button
          variant="ghost"
          size="small"
          disabled={resetDisabled}
          onClick={() => setConfirmReset(true)}
        >
          <Icon name="restart_alt" className="!text-[16px]" /> Reset Wizard
        </Button>
        <span className="text-xs text-base-content/45">
          {resetActive
            ? m.status?.sessionStatus === "cancelling"
              ? "Wait for the current queue submission to drain before resetting the wizard."
              : "Finish or cancel the active task before resetting the wizard."
            : "Clears this wizard session while preserving completed migration mappings."}
        </span>
      </div>
      <div className="mt-1 flex flex-wrap items-center gap-2">
        <Button variant="ghost" size="small" onClick={openManage}>
          <Icon name="database" className="!text-[16px]" /> Manage Migration Data
        </Button>
        <span className="text-xs text-base-content/45">
          View or forget completed migration mappings used for future symlink rewrites.
        </span>
      </div>

      <Modal
        open={manage}
        onClose={() => setManage(false)}
        title="Manage Migration Data"
        footer={
          <Button variant="outline" onClick={() => setManage(false)}>
            Close
          </Button>
        }
      >
        <div className="space-y-4 text-sm">
          <p className="text-base-content/65">
            Completed mappings are kept across wizard resets so symlinks can be rewritten after
            multiple migrations.
          </p>
          <div className="grid grid-cols-3 gap-3">
            <StatTile label="Runs" value={m.migrationData?.runs ?? "…"} />
            <StatTile label="Releases" value={m.migrationData?.releases ?? "…"} />
            <StatTile label="Files" value={m.migrationData?.files ?? "…"} />
          </div>
          <div className="rounded-lg border border-error/35 bg-error/5 p-3">
            <div className="font-medium text-error">Danger zone</div>
            <p className="mt-1 text-xs text-base-content/65">
              Forget all run, release, and file mappings. This removes cross-run symlink provenance,
              but never deletes mounted content or SAB history. Any symlinks already rewritten
              remain safe and unchanged.
            </p>
            <Button
              className="mt-3"
              variant="danger"
              size="small"
              disabled={resetActive || m.busy !== null}
              onClick={() => {
                setManage(false);
                setConfirmForget(true);
              }}
            >
              Forget all migration records
            </Button>
          </div>
        </div>
      </Modal>

      <ConfirmModal
        show={confirmReset}
        title="Reset migration wizard"
        message={
          <>
            This clears the current scan results, category map, symlink plan, and connection.
            Completed migration mappings and all InfiniDysk content are preserved.
          </>
        }
        cancelText="Cancel"
        confirmText="Reset"
        onCancel={() => setConfirmReset(false)}
        onConfirm={() => {
          setConfirmReset(false);
          void m.reset();
        }}
      />
      <ConfirmModal
        show={confirmForget}
        title="Forget all migration records?"
        message={
          <>
            This permanently removes the migration run, release, and file mappings used to connect
            symlinks across runs. Mounted InfiniDysk content, SAB history, and symlinks already
            rewritten will remain safe and unchanged.
          </>
        }
        errorMessage="Future symlink scans cannot identify files from earlier migrations unless they can be rediscovered from live content."
        cancelText="Keep records"
        confirmText="Forget records"
        onCancel={() => setConfirmForget(false)}
        onConfirm={() => {
          setConfirmForget(false);
          void m.forgetMigrationData();
        }}
      />
    </div>
  );
}

function SummaryTiles({ summary }: { summary: SummaryResponse }) {
  return (
    <div className="space-y-3">
      <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
        <StatTile label="Total" value={summary.counts.total} />
        <StatTile label="Green" value={summary.counts.green} tone="success" />
        <StatTile label="Amber" value={summary.counts.amber} tone="warning" />
        <StatTile label="Red" value={summary.counts.red} tone="error" />
        <StatTile label="Will migrate" value={summary.counts.submittable} tone="success" />
        <StatTile label="Already migrated" value={summary.counts.alreadyMigrated} tone="success" />
        <StatTile label="No store (v1)" value={summary.counts.noStoreRef} />
        <StatTile label="Est. fetch (lazy)" value={formatBytes(summary.cost.estFetchBytesLazy)} />
        <StatTile
          label="Scan errors"
          value={summary.warnings.scanErrors}
          tone={summary.warnings.scanErrors > 0 ? "warning" : undefined}
        />
      </div>
      {(summary.warnings.blockingCollisions > 0 || summary.warnings.unmapped > 0) && (
        <Alert className="alert-soft text-sm" variant="warning">
          <Icon name="warning" className="!text-[18px]" />
          {summary.warnings.blockingCollisions > 0 && (
            <span>{summary.warnings.blockingCollisions} blocking collision(s). </span>
          )}
          {summary.warnings.unmapped > 0 && (
            <span>{summary.warnings.unmapped} unmapped categor(y/ies). </span>
          )}
          Resolve these before running.
        </Alert>
      )}
    </div>
  );
}

function StatTile({
  label,
  value,
  tone,
  help,
}: {
  label: string;
  value: number | string;
  tone?: "success" | "warning" | "error" | undefined;
  help?: string | undefined;
}) {
  const toneClass =
    tone === "success"
      ? "text-success"
      : tone === "warning"
        ? "text-warning"
        : tone === "error"
          ? "text-error"
          : "text-base-content";
  const tile = (
    <span
      className={`block rounded-lg border border-base-content/10 bg-base-100 p-3 ${help ? "cursor-help" : ""}`}
    >
      <span className={`block font-mono text-xl font-semibold ${toneClass}`}>{value}</span>
      <span className="block text-[11px] uppercase tracking-wide text-base-content/50">
        {label}
      </span>
    </span>
  );
  return help ? <Tooltip content={help}>{tile}</Tooltip> : tile;
}

function VerdictBadge({ verdict }: { verdict: string }) {
  const cls =
    verdict === "green" ? "badge-success" : verdict === "amber" ? "badge-warning" : "badge-error";
  return <Badge className={`badge-sm ${cls} badge-soft`}>{verdict}</Badge>;
}

function ReasonBadges({ reasons }: { reasons: string[] }) {
  if (!reasons || reasons.length === 0) return null;
  return (
    <span className="flex flex-wrap gap-1">
      {reasons.map((r) => (
        <span
          key={r}
          className="badge badge-xs badge-ghost font-mono"
          title={REASON_LABELS[r] ?? r}
        >
          {REASON_LABELS[r] ?? r}
        </span>
      ))}
    </span>
  );
}

const REASON_LABELS: Record<string, string> = {
  status_degraded: "all files degraded",
  some_files_degraded: "some files degraded",
  known_holes: "known holes",
  v1_source_nzb: "Original NZB on disk (v1)",
};

function Section({
  icon,
  title,
  subtitle,
  children,
}: {
  icon: string;
  title: string;
  subtitle?: string;
  children: React.ReactNode;
}) {
  return (
    <section className="overflow-hidden rounded-lg border border-base-content/10 bg-base-100">
      <div className="flex items-start gap-3 border-b border-base-content/10 p-4">
        <span className="rounded-lg bg-primary/10 p-2 text-primary">
          <Icon name={icon} className="!text-[20px]" />
        </span>
        <div>
          <h2 className="text-sm font-semibold text-base-content">{title}</h2>
          {subtitle && (
            <p className="mt-0.5 text-xs leading-relaxed text-base-content/50">{subtitle}</p>
          )}
        </div>
      </div>
      <div className="p-4">{children}</div>
    </section>
  );
}

function PathField({
  label,
  help,
  value,
  required,
  disabled,
  onChange,
}: {
  label: string;
  help?: string;
  value: string;
  required?: boolean;
  disabled?: boolean;
  onChange: (v: string) => void;
}) {
  return (
    <label className="block space-y-1">
      <span className="block text-sm font-medium text-base-content">
        {label}
        {required && <span className="text-error"> *</span>}
      </span>
      <Input
        className="w-full font-mono"
        value={value}
        disabled={disabled}
        onChange={(e) => onChange(e.target.value)}
        placeholder="/path/to/…"
      />
      {help && (
        <span className="block text-[11px] leading-relaxed text-base-content/45">{help}</span>
      )}
    </label>
  );
}

function NumberField({
  label,
  help,
  value,
  onChange,
  min,
  max,
}: {
  label: string;
  help?: string;
  value: number;
  onChange: (v: number) => void;
  min?: number;
  max?: number;
}) {
  return (
    <label className="block space-y-1">
      <span className="block text-sm font-medium text-base-content">{label}</span>
      <Input
        className="w-full max-w-[10rem]"
        type="number"
        min={min ?? 1}
        max={max}
        value={value}
        onChange={(e) => onChange(parseInt(e.target.value) || (min ?? 1))}
      />
      {help && (
        <span className="block text-[11px] leading-relaxed text-base-content/45">{help}</span>
      )}
    </label>
  );
}

function EmptyHint({ icon, text }: { icon: string; text: string }) {
  return (
    <div className="rounded-lg border border-dashed border-base-content/15 bg-base-200/20 px-4 py-8 text-center">
      <Icon name={icon} className="!text-[28px] text-base-content/35" />
      <p className="mt-2 text-sm text-base-content/55">{text}</p>
    </div>
  );
}

function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return "0 B";
  const units = ["B", "KB", "MB", "GB", "TB"];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value.toFixed(unit === 0 ? 0 : 1)} ${units[unit]}`;
}
