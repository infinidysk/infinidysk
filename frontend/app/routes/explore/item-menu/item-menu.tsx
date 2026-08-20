import { useCallback, useState, type ReactNode } from "react";
import type { ExploreFile } from "../route";
import { DropdownOptions } from "~/components/dropdown-options/dropdown-options";
import { Icon } from "~/components/ui";
import { ExportNzb, Remove } from "~/components/item-action-labels";

export type ItemMenuProps = {
  className?: string;
  openClassName?: string;
  exploreFile?: ExploreFile;
  previewPath?: string;
  /** When set, Preview opens the in-app player instead of navigating. */
  onPreview?: () => void;
  onRemove?: () => void;
};

export function ItemMenu({
  className,
  openClassName,
  exploreFile,
  previewPath,
  onPreview,
  onRemove,
}: ItemMenuProps): ReactNode {
  const [isOpen, setIsOpen] = useState(false);
  const exportNzbUrl = exploreFile
    ? `/api/download-nzb?nzbBlobId=${exploreFile.nzbBlobId}`
    : undefined;
  const downloadUrl = previewPath ? `${previewPath}&download=true` : undefined;

  const onClick = useCallback((e: React.MouseEvent) => {
    e.preventDefault();
    e.stopPropagation();
    setIsOpen((x) => !x);
  }, []);

  const options = [
    onPreview
      ? { option: <Preview />, onSelect: onPreview }
      : previewPath
        ? { option: <Preview />, linkTo: previewPath }
        : undefined,
    downloadUrl ? { option: <Download />, linkTo: downloadUrl } : undefined,
    exploreFile?.nzbBlobId && exportNzbUrl
      ? { option: <ExportNzb />, linkTo: exportNzbUrl }
      : undefined,
    onRemove ? { option: <Remove />, variant: "danger" as const, onSelect: onRemove } : undefined,
  ].filter(Boolean);

  if (options.length === 0) return null;

  return (
    <>
      <button
        type="button"
        aria-label={exploreFile ? `Actions for ${exploreFile.name}` : "Item actions"}
        aria-expanded={isOpen}
        className={`flex shrink-0 select-none items-center self-stretch rounded-r-lg px-5 py-[15px] text-base-content/60 transition-colors hover:bg-base-content/10 hover:text-base-content active:bg-base-content/15 ${isOpen ? `bg-base-content/10 text-base-content ${openClassName ?? ""}` : ""} ${className ?? ""}`}
        onClick={onClick}
      >
        <Icon name="more_horiz" className="!text-[24px]" />
      </button>
      <DropdownOptions isOpen={isOpen} onClose={() => setIsOpen(false)} options={options} />
    </>
  );
}

export function Preview(): ReactNode {
  return (
    <>
      <Icon name="visibility" className="mr-2 !text-[18px]" /> Preview
    </>
  );
}

export function Download(): ReactNode {
  return (
    <>
      <Icon name="download" className="mr-2 !text-[18px]" /> Download
    </>
  );
}
