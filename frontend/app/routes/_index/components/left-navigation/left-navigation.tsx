import { Link, useLocation, useNavigation } from "react-router";
import type React from "react";
import { Fragment, useEffect, useState } from "react";
import { Icon } from "~/components/ui";
import { ServiceProviderNotice } from "~/components/service-provider-notice";
import {
    SETTINGS_TAB_GROUPS,
    parseSettingsTab,
    settingsPath,
    type SettingsTab,
} from "~/routes/settings/settings-tabs";
import {
    isFeatureDisabled,
    isSettingsTabDisabled,
    type NavFeatureId,
    type ServiceProviderConfig,
} from "~/utils/service-provider";

export type LeftNavigationProps = {
    isWatchdogEnabled?: boolean,
    serviceProvider?: ServiceProviderConfig | null,
}

type NavItem = {
    target: string;
    icon: string;
    label: string;
    featureId: NavFeatureId;
};

export function LeftNavigation({
    isWatchdogEnabled,
    serviceProvider,
}: LeftNavigationProps) {
    const location = useLocation();
    const navigation = useNavigation();
    const pathname = navigation.location?.pathname ?? location.pathname;
    const search = navigation.location?.search ?? location.search;
    const isSettingsRoute = pathname.startsWith("/settings");
    const activeSettingsTab = isSettingsRoute
        ? parseSettingsTab(new URLSearchParams(search).get("tab"))
        : null;

    const [settingsOpen, setSettingsOpen] = useState(isSettingsRoute);
    const [providerNoticeOpen, setProviderNoticeOpen] = useState(false);
    useEffect(() => {
        if (isSettingsRoute) setSettingsOpen(true);
    }, [isSettingsRoute]);

    const items: NavItem[] = [
        { target: "/overview", icon: "dashboard", label: "Overview", featureId: "overview" },
        { target: "/queue", icon: "list_alt", label: "Queue", featureId: "queue" },
        ...(isWatchdogEnabled
            ? [{ target: "/watchdog", icon: "monitor_heart", label: "Watchdog", featureId: "watchdog" as const }]
            : []),
        { target: "/watchtower", icon: "cell_tower", label: "Watchtower", featureId: "watchtower" },
        { target: "/explore", icon: "folder_open", label: "Files", featureId: "explore" },
        { target: "/health", icon: "health_and_safety", label: "Health", featureId: "health" },
        { target: "/logs", icon: "description", label: "Logs", featureId: "logs" },
        { target: "/search", icon: "search", label: "Search", featureId: "search" },
    ];

    // Sidebar clicks open the notice without navigating away from the current
    // page, so Close only dismisses the dialog. Deep-link gates use replace to
    // /overview instead (see ServiceProviderGate / Settings).
    const closeProviderNotice = () => {
        setProviderNoticeOpen(false);
    };

    return (
        <div className="flex h-full min-h-0 flex-col gap-4 overflow-y-auto p-4 text-base-content">
            <nav aria-label="Main">
                <ul className="menu menu-md w-full gap-1 p-0 text-[15px]">
                    {items.map((item) => (
                        <Item
                            key={item.target}
                            target={item.target}
                            icon={item.icon}
                            pathname={pathname}
                            disabled={isFeatureDisabled(serviceProvider, item.featureId)}
                            providerName={serviceProvider?.name}
                            onDisabledClick={() => setProviderNoticeOpen(true)}
                        >
                            {item.label}
                        </Item>
                    ))}
                    <li className="mt-1 mb-2">
                        <button
                            type="button"
                            className={[
                                "menu-dropdown-toggle",
                                settingsOpen ? "menu-dropdown-show" : "",
                            ].filter(Boolean).join(" ")}
                            aria-expanded={settingsOpen}
                            onClick={() => setSettingsOpen((open) => !open)}
                        >
                            <Icon
                                name="settings"
                                filled={isSettingsRoute}
                                className="!text-[22px]"
                            />
                            <span className="flex-1 text-left">Settings</span>
                        </button>
                    </li>
                    {settingsOpen && SETTINGS_TAB_GROUPS.map((group) => (
                        <Fragment key={group.title}>
                            <li className="menu-title ms-3 mt-2 border-s border-base-content/10 ps-3">
                                <span className="text-[10px] font-semibold uppercase tracking-wider text-base-content/45">
                                    {group.title}
                                </span>
                            </li>
                            {group.items.map((item) => (
                                <SettingsItem
                                    key={item.id}
                                    tab={item.id}
                                    icon={item.icon}
                                    activeTab={activeSettingsTab}
                                    disabled={isSettingsTabDisabled(serviceProvider, item.id)}
                                    providerName={serviceProvider?.name}
                                    onDisabledClick={() => setProviderNoticeOpen(true)}
                                >
                                    {item.label}
                                </SettingsItem>
                            ))}
                        </Fragment>
                    ))}
                </ul>
            </nav>
            {serviceProvider && (
                <ServiceProviderNotice
                    open={providerNoticeOpen}
                    serviceProvider={serviceProvider}
                    onClose={closeProviderNotice}
                />
            )}
        </div>
    );
}

function Item({
    target,
    icon,
    children,
    pathname,
    disabled,
    providerName,
    onDisabledClick,
}: {
    target: string;
    icon: string;
    children: React.ReactNode;
    pathname: string;
    disabled: boolean;
    providerName?: string;
    onDisabledClick: () => void;
}) {
    const isSelected = pathname.startsWith(target);
    const content = (
        <>
            <Icon name={icon} filled={isSelected} className="!text-[22px]" />
            <span className="flex-1 text-left">{children}</span>
        </>
    );
    return (
        <li>
            {disabled ? (
                <button
                    type="button"
                    aria-disabled="true"
                    aria-label={
                        typeof children === "string"
                            ? `${children} (disabled by ${providerName ?? "your service provider"})`
                            : undefined
                    }
                    className="opacity-50"
                    onClick={onDisabledClick}
                >
                    {content}
                </button>
            ) : (
                <Link
                    to={target}
                    aria-current={isSelected ? "page" : undefined}
                    className={isSelected ? "menu-active" : undefined}
                >
                    {content}
                </Link>
            )}
        </li>
    );
}

function SettingsItem({
    tab,
    icon,
    activeTab,
    children,
    disabled,
    providerName,
    onDisabledClick,
}: {
    tab: SettingsTab;
    icon: string;
    activeTab: SettingsTab | null;
    children: React.ReactNode;
    disabled: boolean;
    providerName?: string;
    onDisabledClick: () => void;
}) {
    const isSelected = activeTab === tab;
    const content = (
        <>
            <Icon name={icon} filled={isSelected} className="!text-[18px]" />
            <span className="flex-1 text-left">{children}</span>
        </>
    );
    return (
        <li className="ms-3 border-s border-base-content/10 ps-1">
            {disabled ? (
                <button
                    type="button"
                    aria-disabled="true"
                    aria-label={
                        typeof children === "string"
                            ? `${children} (disabled by ${providerName ?? "your service provider"})`
                            : undefined
                    }
                    className="text-sm opacity-50"
                    onClick={onDisabledClick}
                >
                    {content}
                </button>
            ) : (
                <Link
                    to={settingsPath(tab)}
                    aria-current={isSelected ? "page" : undefined}
                    className={`text-sm ${isSelected ? "menu-active" : ""}`}
                >
                    {content}
                </Link>
            )}
        </li>
    );
}
