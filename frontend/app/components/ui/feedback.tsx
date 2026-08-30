import {
  cloneElement,
  isValidElement,
  useId,
  useState,
  type HTMLAttributes,
  type ReactNode,
} from "react";

type AlertVariant = "info" | "success" | "warning" | "danger";

const alertVariants: Record<AlertVariant, string> = {
  info: "alert-info",
  success: "alert-success",
  warning: "alert-warning",
  danger: "alert-error",
};

export function Alert({
  variant = "info",
  className = "",
  ...props
}: HTMLAttributes<HTMLDivElement> & { variant?: AlertVariant }) {
  return <div role="alert" className={`alert ${alertVariants[variant]} ${className}`} {...props} />;
}

export function Badge({ className = "", ...props }: HTMLAttributes<HTMLSpanElement>) {
  return <span className={`badge ${className}`} {...props} />;
}

export function Spinner({ className = "", size }: { className?: string; size?: string }) {
  return (
    <span className={`loading loading-spinner ${size === "sm" ? "loading-sm" : ""} ${className}`} />
  );
}

type TooltipPlacement = "top" | "bottom" | "left" | "right";

const tooltipPlacementClass: Record<TooltipPlacement, string> = {
  top: "tooltip-top",
  bottom: "tooltip-bottom",
  left: "tooltip-left",
  right: "tooltip-right",
};

export function Tooltip({
  content,
  children,
  placement = "top",
  className = "",
  contentClassName = "",
}: {
  content: string;
  children: ReactNode;
  placement?: TooltipPlacement;
  className?: string;
  contentClassName?: string;
}) {
  const tooltipId = useId();
  const [hovered, setHovered] = useState(false);
  const [focused, setFocused] = useState(false);
  const open = hovered || focused;
  const trigger = isValidElement<{ "aria-describedby"?: string }>(children)
    ? cloneElement(
        children,
        open
          ? {
              "aria-describedby": [children.props["aria-describedby"], tooltipId]
                .filter(Boolean)
                .join(" "),
            }
          : {},
      )
    : children;

  return (
    <span
      className={[
        "tooltip",
        tooltipPlacementClass[placement],
        open ? "tooltip-open" : "",
        className,
      ]
        .filter(Boolean)
        .join(" ")}
      onPointerEnter={() => setHovered(true)}
      onPointerLeave={() => setHovered(false)}
      onFocusCapture={() => setFocused(true)}
      onBlurCapture={(event) => {
        const next = event.relatedTarget;
        if (!(next instanceof Node) || !event.currentTarget.contains(next)) {
          setFocused(false);
        }
      }}
    >
      <span
        id={tooltipId}
        role="tooltip"
        aria-hidden={!open}
        className={[
          "tooltip-content z-50 w-72 max-w-[calc(100vw-2rem)] whitespace-normal text-left text-xs leading-relaxed",
          contentClassName,
        ]
          .filter(Boolean)
          .join(" ")}
      >
        {content}
      </span>
      {trigger}
    </span>
  );
}
