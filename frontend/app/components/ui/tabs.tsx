import type { ReactNode } from "react";
import { Icon } from "./icon";

export type TabOption<T extends string> = {
  id: T;
  label: string;
  icon?: string;
  disabled?: boolean;
};

export function Tabs<T extends string>({
  options,
  value,
  onChange,
}: {
  options: TabOption<T>[];
  value: T;
  onChange: (value: T) => void;
}) {
  return (
    <div role="tablist" className="tabs tabs-border flex-wrap">
      {options.map((option) => {
        const active = option.id === value;
        return (
          <button
            key={option.id}
            role="tab"
            aria-selected={active}
            disabled={option.disabled}
            onClick={() => onChange(option.id)}
            className={`tab gap-1 md:gap-2 ${active ? "tab-active" : ""} ${
              option.disabled ? "tab-disabled" : ""
            }`}
          >
            {option.icon && <Icon name={option.icon} className="!text-[18px]" />}
            {option.label}
          </button>
        );
      })}
    </div>
  );
}

export function TabPanel({
  children,
  className = "",
}: {
  children: ReactNode;
  className?: string;
}) {
  return <div className={`pt-4 ${className}`}>{children}</div>;
}
