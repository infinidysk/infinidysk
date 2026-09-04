export type BackendProxyAdmissionRequest = {
  requiresMetricsAuthentication: boolean;
  isReadOnlyMutation: boolean;
  userAgent: string | string[] | undefined;
};

type BackendProxyAdmissionHandlers = {
  isAuthenticated: () => Promise<boolean>;
  injectApiKey: () => Promise<void>;
  getRole: () => Promise<string | null>;
  rejectMetrics: () => void;
  rejectReadOnlyMutation: () => void;
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

  if (request.isReadOnlyMutation && (await handlers.getRole()) === "readonly") {
    handlers.rejectReadOnlyMutation();
    return;
  }

  handlers.observeRclone(request.userAgent);
  handlers.forward();
}
