import { useOutletContext } from "react-router";
import type { UserRole } from "./authentication.server";
import type { ServiceProviderConfig } from "~/utils/service-provider";

export type AppOutletContext = {
  role: UserRole | null;
  isOidcEnabled: boolean;
  serviceProvider: ServiceProviderConfig | null;
};

export function useIsReadOnly(): boolean {
  return useOutletContext<AppOutletContext>().role === "readonly";
}
