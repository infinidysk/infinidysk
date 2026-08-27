import { isPositiveInteger } from "../validation";

export type ProviderConnectionLimitDraft = Readonly<{
  providerConnectionLimit: string;
  transferConnections: string;
}>;

export type BenchmarkConnectionLimits = Readonly<{
  providerConnectionLimit: string;
  testConnections: string;
}>;

export function resolveBenchmarkConnectionLimits(
  draft: ProviderConnectionLimitDraft,
  pipeliningOnly: boolean,
): BenchmarkConnectionLimits | null {
  if (!isPositiveInteger(draft.providerConnectionLimit)) return null;

  const providerConnectionLimit = draft.providerConnectionLimit.trim();
  if (!pipeliningOnly) {
    return {
      providerConnectionLimit,
      testConnections: providerConnectionLimit,
    };
  }

  const testConnections =
    draft.transferConnections.trim() === ""
      ? providerConnectionLimit
      : draft.transferConnections.trim();
  if (
    !isPositiveInteger(testConnections) ||
    Number(testConnections) > Number(providerConnectionLimit)
  ) {
    return null;
  }

  return { providerConnectionLimit, testConnections };
}

export function applyAutoTuneTransferRecommendation(
  draft: ProviderConnectionLimitDraft,
  recommendedConnections: number | null | undefined,
  pipeliningOnly: boolean,
  verificationRun: boolean,
): ProviderConnectionLimitDraft {
  if (
    pipeliningOnly ||
    verificationRun ||
    recommendedConnections == null ||
    !Number.isInteger(recommendedConnections) ||
    recommendedConnections <= 0
  ) {
    return draft;
  }

  const providerLimit = Number(draft.providerConnectionLimit);
  if (!Number.isInteger(providerLimit) || providerLimit <= 0) {
    return draft;
  }

  return {
    ...draft,
    transferConnections: String(Math.min(recommendedConnections, providerLimit)),
  };
}
