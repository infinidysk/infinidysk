import type { ReactNode } from "react";
import { useLocation, useNavigate } from "react-router";
import { parseSettingsTab } from "~/navigation/settings-tabs";
import {
  isNavRouteDisabled,
  isSettingsTabDisabled,
  type ServiceProviderConfig,
} from "~/utils/service-provider";
import { ServiceProviderNotice } from "./service-provider-notice";

export type ServiceProviderGateProps = {
  children: ReactNode;
  serviceProvider: ServiceProviderConfig | null | undefined;
};

export function ServiceProviderGate({
  children,
  serviceProvider,
}: ServiceProviderGateProps) {
  const location = useLocation();
  const navigate = useNavigate();

  if (!serviceProvider) {
    return children;
  }

  const isSettingsRoute = location.pathname.startsWith("/settings");
  const activeSettingsTab = parseSettingsTab(
    new URLSearchParams(location.search).get("tab"),
  );
  const isDisabled = isSettingsRoute
    ? isSettingsTabDisabled(serviceProvider, activeSettingsTab)
    : isNavRouteDisabled(serviceProvider, location.pathname);

  if (!isDisabled) {
    return children;
  }

  return (
    <ServiceProviderNotice
      open
      serviceProvider={serviceProvider}
      onClose={() => { void navigate("/overview", { replace: true }); }}
    />
  );
}
