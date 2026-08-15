import {
  isRouteErrorResponse,
  Links,
  Meta,
  Outlet,
  Scripts,
  ScrollRestoration,
  useLocation,
  useNavigation,
  useRouteError,
  type ShouldRevalidateFunctionArgs,
} from "react-router";

import "./app.css";
import type { Route } from "./+types/root";
import { getSessionUser, IS_FRONTEND_AUTH_DISABLED } from "~/auth/authentication.server";
import { TopNavigation } from "./routes/_index/components/top-navigation/top-navigation";
import { LeftNavigation } from "./routes/_index/components/left-navigation/left-navigation";
import { PageLayout } from "./routes/_index/components/page-layout/page-layout";
import { Loading } from "./routes/_index/components/loading/loading";
import { getAppVersion } from "./utils/version.server";
import { checkForUpdate } from "./utils/update-check.server";
import { backendClient } from "./clients/backend-client.server";
import { MigrationBoundary } from "./components/migration-progress";
import { ServiceProviderGate } from "./components/service-provider-gate";
import { StreamTracingBanner } from "./components/stream-tracing-banner";
import { LegacyImageBanner } from "./components/legacy-image-banner";
import { ResetAdminPasswordBanner } from "./components/reset-admin-password-banner";
import { isOidcEnabled } from "../server/oidc.server";
import { getServiceProvider } from "./utils/service-provider.server";
import { withUrlBase } from "~/utils/url-base";

function isResetAdminPasswordSet(): boolean {
  const value = process.env["RESET_ADMIN_PASSWORD"]?.trim().toLowerCase();
  return value === "true" || value === "1" || value === "yes";
}

export async function loader({ request }: Route.LoaderArgs) {
  // Single-fetch navigation/revalidation uses internal `.data` URLs
  // (e.g. /login.data), so strip that suffix before the layout check.
  let path = new URL(request.url).pathname.replace(/\.data$/, "");
  if (path === "/login" || path === "/onboarding") {
    return { useLayout: false, serviceProvider: null };
  }

  const config = await backendClient.getConfig([
    "usenet.providers",
    "play.watchdog-enabled",
  ]);

  const version = await getAppVersion();
  const sessionUser = await getSessionUser(request);
  const serviceProvider = getServiceProvider();

  return {
    useLayout: true,
    // Baked into images published to the deprecated ghcr.io/nzbdav/nzbdav path.
    isLegacyImage: process.env["NZBDAV_LEGACY_IMAGE"] === "true",
    isResetAdminPasswordSet: isResetAdminPasswordSet(),
    version,
    updateAvailable: await checkForUpdate(version),
    isFrontendAuthDisabled: IS_FRONTEND_AUTH_DISABLED,
    username: sessionUser?.username ?? null,
    role: sessionUser?.role ?? null,
    isOidcEnabled: isOidcEnabled(),
    hasUsenetProviders: hasConfiguredUsenetProviders(
      config.find(item => item.configName === "usenet.providers")?.configValue
    ),
    isWatchdogEnabled:
      config.find(item => item.configName === "play.watchdog-enabled")?.configValue?.toLowerCase() !== "false",
    serviceProvider,
  };
}

/** True for pages the root loader renders without the app layout. */
function isLayoutlessPath(pathname: string): boolean {
  const path = pathname.replace(/\.data$/, "");
  return path === "/login" || path === "/onboarding";
}

/**
 * Skip root config re-fetches for routine mutations (queue deletes, toggles),
 * but always revalidate when crossing the login/onboarding layout boundary
 * (login/logout redirects change `useLayout`) and after settings/onboarding
 * saves (which can change providers/watchdog config).
 */
export function shouldRevalidate({
  currentUrl,
  nextUrl,
  formMethod,
  defaultShouldRevalidate,
}: ShouldRevalidateFunctionArgs) {
  if (isLayoutlessPath(currentUrl.pathname) !== isLayoutlessPath(nextUrl.pathname)) {
    return true;
  }
  if (formMethod && formMethod !== "GET") {
    const fromSettingsOrOnboarding =
      currentUrl.pathname.startsWith("/settings")
      || currentUrl.pathname.startsWith("/onboarding");
    const toSettingsOrOnboarding =
      nextUrl.pathname.startsWith("/settings")
      || nextUrl.pathname.startsWith("/onboarding");
    return fromSettingsOrOnboarding || toSettingsOrOnboarding;
  }
  return defaultShouldRevalidate;
}

function hasConfiguredUsenetProviders(configValue?: string): boolean {
  if (!configValue) return false;

  try {
    const config: unknown = JSON.parse(configValue);
    return typeof config === "object"
      && config !== null
      && "Providers" in config
      && Array.isArray(config.Providers)
      && config.Providers.length > 0;
  } catch {
    return false;
  }
}


export function Layout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en" data-theme="night">
      <head>
        <meta charSet="utf-8" />
        <meta name="viewport" content="width=device-width, initial-scale=1" />
        <title>InfiniDysk</title>
        <meta
          name="description"
          content="The NzbDAV SuperFork — stream media directly from Usenet."
        />
        <link rel="apple-touch-icon" sizes="180x180" href={withUrlBase("/apple-touch-icon.png")} />
        <link rel="icon" type="image/png" sizes="32x32" href={withUrlBase("/favicon-32x32.png")} />
        <link rel="icon" type="image/png" sizes="16x16" href={withUrlBase("/favicon-16x16.png")} />
        <link rel="icon" href={withUrlBase("/favicon.ico")} />
        <link rel="manifest" href={withUrlBase("/site.webmanifest")} />
        <Meta />
        <Links />
      </head>
      <body>
        {children}
        <ScrollRestoration />
        <Scripts />
      </body>
    </html>
  );
}

export default function App({ loaderData }: Route.ComponentProps) {
  const {
    useLayout,
    isLegacyImage,
    isResetAdminPasswordSet,
    version,
    updateAvailable,
    isFrontendAuthDisabled,
    username,
    role,
    isOidcEnabled,
    hasUsenetProviders,
    isWatchdogEnabled,
    serviceProvider,
  } = loaderData;
  const location = useLocation();
  const navigation = useNavigation();
  const isNavigating = Boolean(navigation.location);

  // display loading animiation during top-level page transitions,
  // but allow the `/explore` page to handle it's own loading screen.
  const isCurrentExplorePage = location.pathname.startsWith("/explore");
  const isNextExplorePage = navigation.location?.pathname?.startsWith("/explore");
  const showLoading = isNavigating && !(isCurrentExplorePage && isNextExplorePage);
  const hideShell =
    location.pathname === "/login" || location.pathname === "/onboarding";
  const outlet = <Outlet context={{ role, isOidcEnabled, serviceProvider }} />;

  if (useLayout && !hideShell) {
    return (
      <PageLayout
        topNavComponent={(navProps) => (
          <TopNavigation
            {...navProps}
            {...(version !== undefined ? { version } : {})}
            {...(updateAvailable !== undefined ? { updateAvailable } : {})}
            {...(isFrontendAuthDisabled !== undefined ? { isFrontendAuthDisabled } : {})}
            {...(username !== undefined ? { username } : {})}
            {...(hasUsenetProviders !== undefined ? { hasUsenetProviders } : {})}
          />
        )}
        bodyChild={
          <ServiceProviderGate serviceProvider={serviceProvider}>
            {showLoading ? <Loading /> : (
              <>
                <div className="px-4 pt-4 md:px-8">
                  <ResetAdminPasswordBanner
                    isResetAdminPasswordSet={isResetAdminPasswordSet ?? false}
                  />
                  <LegacyImageBanner isLegacyImage={isLegacyImage ?? false} />
                  <StreamTracingBanner isReadOnly={role === "readonly"} />
                </div>
                {outlet}
              </>
            )}
          </ServiceProviderGate>
        }
        leftNavChild={
          <LeftNavigation
            {...(isWatchdogEnabled !== undefined ? { isWatchdogEnabled } : {})}
            serviceProvider={serviceProvider}
          />
        }
        serviceProvider={serviceProvider}
      />
    );
  }

  return outlet;
}

// Root ErrorBoundary catches loader/component throws that aren't handled closer
// to the route. Without this, an SSR loader that rejects (e.g. backend fetch
// timeout while the backend is busy) bubbles to React Router's default 500 with
// no UI. Keep this page free of PageLayout so we don't re-run the root loader
// and loop back into the same failure. Adopted from elfhosted/rebased-v3.
export function ErrorBoundary() {
  const error = useRouteError();
  // Match by name — do not import BackendUnavailableError here; ErrorBoundary is a
  // client export and .server modules must only be referenced from loader/action.
  const isBackendUnavailable =
    (error instanceof Error && error.name === "BackendUnavailableError")
    || (
      error instanceof Error
      && /fetch failed|ConnectTimeoutError|HeadersTimeoutError|UND_ERR_CONNECT_TIMEOUT|UND_ERR_HEADERS_TIMEOUT/i.test(
        `${error.message} ${(error.cause as Error)?.message ?? ""}`,
      )
    );

  let title = "Something went wrong";
  let detail: string;
  if (isBackendUnavailable) {
    title = "Backend temporarily unavailable";
    detail =
      "The InfiniDysk backend is still starting up or is busy processing a large queue. Wait a moment and refresh the page.";
  } else if (isRouteErrorResponse(error)) {
    title = `${error.status} ${error.statusText}`;
    detail = typeof error.data === "string" ? error.data : "";
  } else if (error instanceof Error) {
    detail = error.message;
  } else {
    detail = "Unknown error.";
  }

  // A loader throw is also how the app surfaces the blocking database-migration
  // phase (the backend only serves /api/migration-status then). MigrationBoundary
  // polls that endpoint and shows live progress, falling back to this error card.
  return <MigrationBoundary fallback={{ title, detail, showReload: true }} />;
}