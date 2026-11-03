import { useOutletContext } from "react-router";
import type { UserRole, ViewerProfileSession } from "./authentication.server";
import type { ServiceProviderConfig } from "~/utils/service-provider";

export type AppOutletContext = {
  role: UserRole | null;
  isOidcEnabled: boolean;
  serviceProvider: ServiceProviderConfig | null;
  /** The picked viewer profile; null for admin/readonly sessions. */
  viewerProfile: ViewerProfileSession | null;
};

export function useIsReadOnly(): boolean {
  return useOutletContext<AppOutletContext>().role === "readonly";
}
