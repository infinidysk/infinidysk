export function buildSetupRedirect(
  pathname: string,
  search: string,
  canConfigure: boolean,
  setupRequired: boolean,
): string | null {
  if (!canConfigure || !setupRequired || pathname === "/setup") return null;

  const returnTo = pathname === "/" ? "/overview" : `${pathname}${search}`;
  return `/setup?returnTo=${encodeURIComponent(returnTo)}`;
}
