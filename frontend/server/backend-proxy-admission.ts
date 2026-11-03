export type BackendProxyAdmissionRequest = {
  requiresMetricsAuthentication: boolean;
  isReadOnlyMutation: boolean;
  /** True when a profile session asked for a path outside its catalog allowlist. */
  isViewerProfileRestricted: boolean;
  userAgent: string | string[] | undefined;
};

type BackendProxyAdmissionHandlers = {
  isAuthenticated: () => Promise<boolean>;
  injectApiKey: () => Promise<void>;
  getRole: () => Promise<string | null>;
  applyViewerProfileScope: () => Promise<void>;
  rejectMetrics: () => void;
  rejectReadOnlyMutation: () => void;
  rejectViewerProfileScope: () => void;
  observeRclone: (userAgent: string | string[] | undefined) => void;
  forward: () => void;
};

export async function admitAndForwardBackendRequest(
  request: BackendProxyAdmissionRequest,
  handlers: BackendProxyAdmissionHandlers,
): Promise<void> {
  if (request.requiresMetricsAuthentication && !(await handlers.isAuthenticated())) {
    handlers.rejectMetrics();
    return;
  }

  await handlers.injectApiKey();

  const role = await handlers.getRole();

  if (request.isViewerProfileRestricted && role === "profile") {
    handlers.rejectViewerProfileScope();
    return;
  }

  if (request.isReadOnlyMutation && role === "readonly") {
    handlers.rejectReadOnlyMutation();
    return;
  }

  // Attaches the session's picked profile so watch state stays per-profile, and drops any
  // client-supplied value on every other request.
  await handlers.applyViewerProfileScope();

  handlers.observeRclone(request.userAgent);
  handlers.forward();
}
