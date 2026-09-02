import type { ShouldRevalidateFunctionArgs } from "react-router";

/** True for pages the root loader renders without the app layout. */
export function isLayoutlessPath(pathname: string): boolean {
  const path = pathname.replace(/\.data$/, "");
  return path === "/login" || path === "/onboarding";
}

/**
 * Skip root config re-fetches for routine mutations (queue deletes, toggles)
 * and same-URL revalidation (queue websocket refresh, health/explore poll),
 * but always revalidate when crossing the login/onboarding layout boundary
 * (login/logout redirects change `useLayout`) and after settings/onboarding
 * saves (which can change providers/watchdog config).
 *
 * Sidebar navigations to a different path still use `defaultShouldRevalidate`
 * so session, update-check, and `hasUsenetProviders` stay current.
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
    const fromConfigFlow =
      currentUrl.pathname.startsWith("/settings") ||
      currentUrl.pathname.startsWith("/onboarding") ||
      currentUrl.pathname.startsWith("/setup");
    const toConfigFlow =
      nextUrl.pathname.startsWith("/settings") ||
      nextUrl.pathname.startsWith("/onboarding") ||
      nextUrl.pathname.startsWith("/setup");
    return fromConfigFlow || toConfigFlow;
  }
  if (currentUrl.pathname === nextUrl.pathname && currentUrl.search === nextUrl.search) {
    return false;
  }
  return defaultShouldRevalidate;
}
