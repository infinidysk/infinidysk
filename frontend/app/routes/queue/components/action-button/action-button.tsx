import type { ReactNode } from "react";
import { Button, Icon, PortalTooltip } from "~/components/ui";

const ACTIONS = {
  delete: { icon: "delete", label: "Remove" },
  "move-top": { icon: "vertical_align_top", label: "Move to top" },
  "move-up": { icon: "keyboard_arrow_up", label: "Move up" },
  "move-down": { icon: "keyboard_arrow_down", label: "Move down" },
  retry: { icon: "refresh", label: "Retry" },
} as const;

export const actionIconClass = "btn btn-ghost btn-sm btn-square max-sm:size-11";

export type ActionButtonProps = {
  type: keyof typeof ACTIONS;
  /** Tooltip text; the accessible name appends `subject` when given. */
  label?: string;
  subject?: string;
  disabled?: boolean;
  onClick?: (e: React.MouseEvent) => void;
};

export function ActionButton({
  type,
  label,
  subject,
  disabled,
  onClick,
}: ActionButtonProps): ReactNode {
  const action = ACTIONS[type];
  const text = label ?? action.label;
  return (
    <PortalTooltip content={text}>
      <Button
        variant="ghost"
        disabled={disabled}
        aria-label={subject ? `${text} ${subject}` : text}
        className={`btn-square max-sm:size-11 ${type === "delete" ? "hover:text-error" : ""}`}
        onClick={onClick}
      >
        <Icon name={action.icon} className="!text-[18px]" />
      </Button>
    </PortalTooltip>
  );
}

/** Keeps row action columns aligned when an action does not apply. */
export function ActionSpacer() {
  return <span aria-hidden="true" className="inline-block size-8 shrink-0 max-[899px]:hidden" />;
}
