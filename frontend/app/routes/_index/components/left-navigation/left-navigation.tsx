import { Link, useLocation, useNavigation } from "react-router";
import type React from "react";
import { useEffect, useState } from "react";
import { Icon } from "~/components/ui";
import {
    SETTINGS_TAB_GROUPS,
    parseSettingsTab,
    settingsPath,
    type SettingsTab,
} from "~/routes/settings/settings-tabs";

export type LeftNavigationProps = {
    isWatchdogEnabled?: boolean,
}

type NavItem = {
    target: string;
    icon: string;
    label: string;
};

const SETTINGS_ITEMS = SETTINGS_TAB_GROUPS.flatMap((group) => group.items);

export function LeftNavigation({
    isWatchdogEnabled,
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
    useEffect(() => {
        if (isSettingsRoute) setSettingsOpen(true);
    }, [isSettingsRoute]);

    const items: NavItem[] = [
        { target: "/overview", icon: "dashboard", label: "Overview" },
        { target: "/queue", icon: "list_alt", label: "Queue" },
        ...(isWatchdogEnabled
            ? [{ target: "/watchdog", icon: "monitor_heart", label: "Watchdog" }]
            : []),
        { target: "/watchtower", icon: "cell_tower", label: "Watchtower" },
        { target: "/explore", icon: "folder_open", label: "Files" },
        { target: "/health", icon: "health_and_safety", label: "Health" },
        { target: "/logs", icon: "description", label: "Logs" },
        { target: "/search", icon: "search", label: "Search" },
    ];

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
                    {settingsOpen && SETTINGS_ITEMS.map((item) => (
                        <SettingsItem
                            key={item.id}
                            tab={item.id}
                            icon={item.icon}
                            activeTab={activeSettingsTab}
                        >
                            {item.label}
                        </SettingsItem>
                    ))}
                </ul>
            </nav>
        </div>
    );
}

function Item({
    target,
    icon,
    children,
    pathname,
}: {
    target: string;
    icon: string;
    children: React.ReactNode;
    pathname: string;
}) {
    const isSelected = pathname.startsWith(target);
    return (
        <li>
            <Link
                to={target}
                aria-current={isSelected ? "page" : undefined}
                className={isSelected ? "menu-active" : undefined}
            >
                <Icon name={icon} filled={isSelected} className="!text-[22px]" />
                <span className="flex-1 text-left">{children}</span>
            </Link>
        </li>
    );
}

function SettingsItem({
    tab,
    icon,
    activeTab,
    children,
}: {
    tab: SettingsTab;
    icon: string;
    activeTab: SettingsTab | null;
    children: React.ReactNode;
}) {
    const isSelected = activeTab === tab;
    return (
        <li className="ms-3 border-s border-base-content/10 ps-1">
            <Link
                to={settingsPath(tab)}
                aria-current={isSelected ? "page" : undefined}
                className={`text-sm ${isSelected ? "menu-active" : ""}`}
            >
                <Icon name={icon} filled={isSelected} className="!text-[18px]" />
                <span className="flex-1 text-left">{children}</span>
            </Link>
        </li>
    );
}
