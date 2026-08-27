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

/** A labeled settings group using daisyUI card markup. */
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
      className={`card card-border border-base-content/10 bg-base-100 shadow-md ${className}`}
    >
      <div className="card-body gap-0 p-0">
        <div className="flex items-start gap-3 border-b border-base-content/10 p-4">
          <span className="inline-flex size-9 shrink-0 items-center justify-center rounded-lg bg-base-200 text-base-content">
            <Icon name={icon} className="!text-[20px]" />
          </span>
          <div className="min-w-0 flex-1">
            <h2 className="card-title text-lg font-semibold">{title}</h2>
            <p className="mt-0.5 text-xs leading-relaxed text-base-content/50">{description}</p>
          </div>
          {action && <div className="card-actions shrink-0">{action}</div>}
        </div>
        <div className={`p-4 ${contentClassName}`}>{children}</div>
      </div>
    </section>
  );
}
