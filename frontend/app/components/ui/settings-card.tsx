import type { ReactNode } from "react";
import { Icon } from "./icon";

type SettingsCardProps = {
  icon: string;
  title: string;
  description: ReactNode;
  children: ReactNode;
  action?: ReactNode;
  className?: string;
  contentClassName?: string;
};

/** A labeled settings group with a fixed-size Material Symbol header icon. */
export function SettingsCard({
  icon,
  title,
  description,
  children,
  action,
  className = "",
  contentClassName = "space-y-4",
}: SettingsCardProps) {
  return (
    <section
      className={`overflow-hidden rounded-lg border border-base-content/10 bg-base-100 ${className}`}
    >
      <div className="flex items-start gap-3 border-b border-base-content/10 p-4">
        <span className="inline-flex size-9 shrink-0 items-center justify-center rounded-lg bg-primary/10 text-primary">
          <Icon name={icon} className="!text-[20px]" />
        </span>
        <div className="min-w-0 flex-1">
          <h2 className="text-sm font-semibold text-base-content">{title}</h2>
          <p className="mt-0.5 text-xs leading-relaxed text-base-content/50">{description}</p>
        </div>
        {action && <div className="shrink-0">{action}</div>}
      </div>
      <div className={`p-4 ${contentClassName}`}>{children}</div>
    </section>
  );
}
