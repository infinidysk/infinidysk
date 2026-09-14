export function parseTrustProxyEnvironment(value: string | undefined): boolean | undefined {
  if (value === undefined) return undefined;
  const normalized = value.trim().toLowerCase();
  return normalized === "1" || normalized === "true" || normalized === "yes";
}

export function getTrustProxyEnvironmentOverride(): boolean | undefined {
  return parseTrustProxyEnvironment(process.env["TRUST_PROXY"]);
}
