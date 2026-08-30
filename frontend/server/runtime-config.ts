export type FrontendRuntimeConfig = Readonly<{
  frontendBackendApiKey: string;
}>;

export const FRONTEND_BACKEND_API_KEY_ERROR =
  "Invalid frontend configuration: FRONTEND_BACKEND_API_KEY must be a non-empty value shared with the backend.";

const ALREADY_INITIALIZED_ERROR = "Frontend runtime configuration is already initialized.";
const NOT_INITIALIZED_ERROR = "Frontend runtime configuration has not been initialized.";

export function readFrontendRuntimeConfig(environment: NodeJS.ProcessEnv): FrontendRuntimeConfig {
  const frontendBackendApiKey = environment["FRONTEND_BACKEND_API_KEY"];
  if (frontendBackendApiKey === undefined || frontendBackendApiKey.trim().length === 0) {
    throw new Error(FRONTEND_BACKEND_API_KEY_ERROR);
  }

  return Object.freeze({ frontendBackendApiKey });
}

let installedConfig: FrontendRuntimeConfig | undefined;

export function installFrontendRuntimeConfig(config: FrontendRuntimeConfig): void {
  if (installedConfig && installedConfig.frontendBackendApiKey !== config.frontendBackendApiKey) {
    throw new Error(ALREADY_INITIALIZED_ERROR);
  }
  installedConfig ??= Object.freeze({ ...config });
}

export function getFrontendRuntimeConfig(): FrontendRuntimeConfig {
  if (!installedConfig) {
    throw new Error(NOT_INITIALIZED_ERROR);
  }
  return installedConfig;
}
