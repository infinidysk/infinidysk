import { type ReactNode } from "react";
import { Icon } from "~/components/ui";

export function ExportNzb(): ReactNode {
  return (
    <>
      <Icon name="upload" className="mr-2 !text-[18px]" /> Export NZB
    </>
  );
}

export function Remove(): ReactNode {
  return (
    <>
      <Icon name="delete" className="mr-2 !text-[18px]" /> Remove
    </>
  );
}
