export type ProviderConnectionLimitDraft = Readonly<{
  providerConnectionLimit: string;
  transferConnections: string;
}>;

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

  return {
    ...draft,
    transferConnections: String(recommendedConnections),
  };
}
