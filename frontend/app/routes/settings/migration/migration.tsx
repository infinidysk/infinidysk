import { useState, type ComponentType } from "react";
import { SettingsIntro, SettingsPage } from "~/components/ui";
import { Alert, Badge } from "~/components/ui/feedback";
import { Icon } from "~/components/ui/icon";
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
    description:
      "Import an existing AltMount library by rebuilding NZBs and submitting them through InfiniDysk's normal queue.",
    icon: "moving",
    component: AltmountMigration,
  },
];

export function Migration() {
  // MIGRATION_SOURCES is a non-empty const literal; [0] is always defined.
  const [sourceId, setSourceId] = useState<MigrationSourceId>(MIGRATION_SOURCES[0]!.id);
  const source = MIGRATION_SOURCES.find((s) => s.id === sourceId) ?? MIGRATION_SOURCES[0]!;
  const SourceWizard = source.component;

  return (
    <SettingsPage>
      <SettingsIntro>
        <span className="mr-2 inline-flex align-middle">
          <Badge className="badge-sm badge-warning badge-soft">Experimental</Badge>
        </span>
        Import an existing library from another downloader. This wizard is experimental — keep
        AltMount available until you have verified playback, and back up{" "}
        <code className="font-mono text-xs">/config</code> first.
      </SettingsIntro>

      <Alert className="alert-soft text-sm" variant="warning">
        <Icon name="science" className="!text-[18px]" />
        <span>
          Experimental feature. Expect rough edges; report issues with a support pack. The migration
          ledger is disposable and separate from Backup &amp; Restore.
        </span>
      </Alert>

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
