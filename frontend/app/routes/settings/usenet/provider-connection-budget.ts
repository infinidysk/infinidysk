export type ProviderConnectionBudgetPreview = Readonly<{
  providerLimit: number;
  transferLimit: number;
  baseMetadataCapacity: number;
  metadataBurstAllowance: number;
  maxMetadataCapacity: number;
}>;

export function calculateProviderConnectionBudget(
  providerLimit: number,
  transferLimit: number,
): ProviderConnectionBudgetPreview | null {
  if (
    !Number.isInteger(providerLimit) ||
    providerLimit <= 0 ||
    !Number.isInteger(transferLimit) ||
    transferLimit <= 0 ||
    transferLimit > providerLimit
  ) {
    return null;
  }

  const baseMetadataCapacity = providerLimit - transferLimit;
  const calculatedMetadataMax = baseMetadataCapacity + Math.floor(transferLimit / 2);
  const maxMetadataCapacity = Math.min(providerLimit, Math.max(1, calculatedMetadataMax));
  const metadataBurstAllowance = maxMetadataCapacity - baseMetadataCapacity;

  return {
    providerLimit,
    transferLimit,
    baseMetadataCapacity,
    metadataBurstAllowance,
    maxMetadataCapacity,
  };
}

export function formatMetadataCapacity(
  budget: Pick<ProviderConnectionBudgetPreview, "baseMetadataCapacity" | "maxMetadataCapacity">,
): string {
  return `${budget.baseMetadataCapacity}\u2013${budget.maxMetadataCapacity} connections`;
}
