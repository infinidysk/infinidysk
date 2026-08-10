import type { ReactNode } from "react";
import { Button, Icon } from "~/components/ui";

export type ActionButtonProps = {
    type: "delete" | "explore" | "menu" | "move-top" | "retry" | "pause" | "resume",
    text?: string,
    disabled?: boolean,
    selected?: boolean,
    onClick?: (e: React.MouseEvent) => void,
}

export function ActionButton({ type, text, disabled, selected, onClick }: ActionButtonProps): ReactNode {
    const variant = type === "delete" ? "danger" : type === "explore" ? "warning" : "secondary";
    const icon = type === "delete" ? "delete"
        : type === "explore" ? "folder"
        : type === "move-top" ? "vertical_align_top"
        : type === "retry" ? "refresh"
        : type === "pause" ? "pause"
        : type === "resume" ? "play_arrow"
        : "more_horiz";

    return (
        <Button
            variant={variant}
            size="xsmall"
            disabled={disabled}
            aria-pressed={type === "menu" ? selected : undefined}
            aria-label={!text ? (type === "move-top" ? "Move to top" : type === "retry" ? "Retry" : type === "pause" ? "Pause" : type === "resume" ? "Resume" : type) : undefined}
            className={`${type === "menu" ? "w-[30px] px-1" : ""} ${selected ? "bg-base-content/20 text-base-content" : ""}`}
            onClick={onClick}
        >
            <Icon name={icon} filled={type !== "menu"} className="!text-[16px]" />
            {text && <span>{text}</span>}
        </Button>
    )
}
