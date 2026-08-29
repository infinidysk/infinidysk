import type { HTMLAttributes, ReactNode } from "react";

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
}: {
  content: string;
  children: ReactNode;
  placement?: TooltipPlacement;
  className?: string;
}) {
  return (
    <span className={`tooltip ${tooltipPlacementClass[placement]} ${className}`.trim()}>
      <span className="tooltip-content z-50 w-72 max-w-[calc(100vw-2rem)] whitespace-normal text-left text-xs leading-relaxed">
        {content}
      </span>
      {children}
    </span>
  );
}
