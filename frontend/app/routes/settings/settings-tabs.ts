export type SettingsTab =
    | "usenet"
    | "indexers"
    | "profiles"
    | "queue"
    | "sabnzbd"
    | "streaming"
    | "webdav"
    | "watchdog"
    | "preflight"
    | "watchtower"
    | "warden"
    | "arrs"
    | "rclone"
    | "repairs"
    | "maintenance"
    | "backup"
    | "support"
    | "migration";

export type SettingsTabItem = {
    id: SettingsTab;
    label: string;
    icon: string;
};

export type SettingsTabGroup = {
    title: string;
    items: SettingsTabItem[];
};

export const SETTINGS_TAB_GROUPS: SettingsTabGroup[] = [
    {
        title: "Providers & Search",
        items: [
            { id: "usenet", label: "Usenet", icon: "cloud" },
            { id: "indexers", label: "Indexers", icon: "travel_explore" },
            { id: "profiles", label: "Search Profiles", icon: "tune" },
        ],
    },
    {
        title: "Queue & Import",
        items: [
            { id: "queue", label: "Queue", icon: "list_alt" },
            { id: "sabnzbd", label: "SABnzbd", icon: "download" },
        ],
    },
    {
        title: "Playback & Files",
        items: [
            { id: "streaming", label: "Streaming", icon: "play_circle" },
            { id: "webdav", label: "WebDAV", icon: "folder_shared" },
        ],
    },
    {
        title: "Automation",
        items: [
            { id: "watchdog", label: "Watchdog", icon: "monitor_heart" },
            { id: "preflight", label: "Preflight", icon: "fact_check" },
            { id: "watchtower", label: "Watchtower", icon: "cell_tower" },
            { id: "warden", label: "Warden", icon: "shield" },
        ],
    },
    {
        title: "Integrations",
        items: [
            { id: "arrs", label: "Arr Apps", icon: "sync_alt" },
            { id: "rclone", label: "Rclone Server", icon: "dns" },
        ],
    },
    {
        title: "System",
        items: [
            { id: "repairs", label: "Health & Repairs", icon: "build" },
            { id: "maintenance", label: "Maintenance", icon: "settings_suggest" },
            { id: "backup", label: "Backup & Restore", icon: "settings_backup_restore" },
            { id: "support", label: "Support", icon: "support_agent" },
            { id: "migration", label: "Migration (experimental)", icon: "moving" },
        ],
    },
];

export const DEFAULT_SETTINGS_TAB: SettingsTab = "usenet";

const SETTINGS_TAB_IDS = new Set<string>(
    SETTINGS_TAB_GROUPS.flatMap((group) => group.items.map((item) => item.id)),
);

export function parseSettingsTab(value: string | null | undefined): SettingsTab {
    if (value && SETTINGS_TAB_IDS.has(value)) {
        return value as SettingsTab;
    }
    return DEFAULT_SETTINGS_TAB;
}

export function getSettingsTabItem(tab: SettingsTab): SettingsTabItem {
    return SETTINGS_TAB_GROUPS.flatMap((group) => group.items).find((item) => item.id === tab)!;
}

export function settingsPath(tab: SettingsTab = DEFAULT_SETTINGS_TAB): string {
    return tab === DEFAULT_SETTINGS_TAB ? "/settings" : `/settings?tab=${tab}`;
}
