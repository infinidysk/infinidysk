import {
  cloneElement,
  isValidElement,
  useEffect,
  useId,
  useLayoutEffect,
  useRef,
  useState,
  type CSSProperties,
  type HTMLAttributes,
  type ReactNode,
} from "react";
import { createPortal } from "react-dom";

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

// Portaled so scrolling table viewports and cards cannot clip it.
export function PortalTooltip({
  content,
  children,
  describe = true,
}: {
  content: string;
  children: ReactNode;
  describe?: boolean;
}) {
  const id = useId();
  const anchor = useRef<HTMLSpanElement>(null);
  const bubble = useRef<HTMLSpanElement>(null);
  const [hovered, setHovered] = useState(false);
  const [focused, setFocused] = useState(false);
  const [dismissed, setDismissed] = useState(false);
  const open = (hovered || focused) && !dismissed;
  const [position, setPosition] = useState<CSSProperties>({ visibility: "hidden" });
  useLayoutEffect(() => {
    if (!open || !anchor.current || !bubble.current) return;
    const target = anchor.current.getBoundingClientRect();
    const { width, height } = bubble.current.getBoundingClientRect();
    const gap = 6;
    const top = target.top - height - gap >= 0 ? target.top - height - gap : target.bottom + gap;
    const left = Math.min(
      Math.max(gap, target.left + target.width / 2 - width / 2),
      window.innerWidth - width - gap,
    );
    setPosition({ top, left });
  }, [open, content]);
  useEffect(() => {
    if (!open) return;
    const close = () => setDismissed(true);
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") close();
    };
    window.addEventListener("scroll", close, true);
    window.addEventListener("keydown", onKeyDown);
    return () => {
      window.removeEventListener("scroll", close, true);
      window.removeEventListener("keydown", onKeyDown);
    };
  }, [open]);
  const show = () => {
    if (!open) setPosition({ visibility: "hidden" });
    setDismissed(false);
  };
  const trigger =
    describe && open && isValidElement<{ "aria-describedby"?: string }>(children)
      ? cloneElement(children, {
          "aria-describedby": [children.props["aria-describedby"], id].filter(Boolean).join(" "),
        })
      : children;
  return (
    <span
      ref={anchor}
      className="inline-flex"
      onPointerEnter={() => {
        setHovered(true);
        show();
      }}
      onPointerLeave={() => setHovered(false)}
      onFocusCapture={() => {
        setFocused(true);
        show();
      }}
      onBlurCapture={(event) => {
        const next = event.relatedTarget;
        if (!(next instanceof Node) || !event.currentTarget.contains(next)) {
          setFocused(false);
        }
      }}
    >
      {trigger}
      {open &&
        createPortal(
          <span
            ref={bubble}
            id={id}
            role="tooltip"
            style={position}
            className="pointer-events-none fixed z-[1000] w-max max-w-[min(18rem,calc(100vw-1rem))] whitespace-pre-line rounded-field bg-neutral px-2 py-1 text-left text-xs leading-snug text-neutral-content shadow-lg"
          >
            {content}
          </span>,
          document.body,
        )}
    </span>
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
          "tooltip-content z-50 w-max max-w-[min(18rem,calc(100vw-2rem))] whitespace-normal break-words text-left text-xs leading-relaxed",
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
