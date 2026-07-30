import { useState, type ComponentType } from "react";
import { SettingsIntro, SettingsPage } from "~/components/ui";
import { Tabs, TabPanel } from "~/components/ui/tabs";
import { AltmountMigration } from "./altmount/altmount-migration";

type MigrationSourceId = "altmount";

type MigrationSource = {
    id: MigrationSourceId;
    label: string;
    description: string;
    icon: string;
    component: ComponentType;
};

const MIGRATION_SOURCES: MigrationSource[] = [
    {
        id: "altmount",
        label: "AltMount",
        description: "Import an existing AltMount library by rebuilding NZBs and submitting them through NzbDAV's normal queue.",
        icon: "moving",
        component: AltmountMigration,
    },
];

export function Migration() {
    const [sourceId, setSourceId] = useState<MigrationSourceId>(MIGRATION_SOURCES[0].id);
    const source = MIGRATION_SOURCES.find((s) => s.id === sourceId) ?? MIGRATION_SOURCES[0];
    const SourceWizard = source.component;

    return (
        <SettingsPage>
            <SettingsIntro>
                Migration — import an existing library from another downloader. Pick a source below, then follow that source's guided wizard.
            </SettingsIntro>

            <div className="space-y-2">
                <Tabs
                    options={MIGRATION_SOURCES.map((s) => ({
                        id: s.id,
                        label: s.label,
                        icon: s.icon,
                    }))}
                    value={sourceId}
                    onChange={setSourceId}
                />
                <p className="text-xs leading-relaxed text-base-content/50">{source.description}</p>
            </div>

            <TabPanel>
                <SourceWizard />
            </TabPanel>
        </SettingsPage>
    );
}
