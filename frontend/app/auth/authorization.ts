import { useOutletContext } from "react-router";
import type { UserRole } from "./authentication.server";

export type AppOutletContext = {
  role: UserRole | null;
  isOidcEnabled: boolean;
};

export function useIsReadOnly(): boolean {
  return useOutletContext<AppOutletContext>().role === "readonly";
}
