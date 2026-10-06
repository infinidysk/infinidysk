import { useEffect, useRef, type ReactNode } from "react";
import { Checkbox } from "~/components/ui";

export type TriCheckboxState = "all" | "some" | "none" | boolean;
export type TriCheckboxProps = {
  state: TriCheckboxState;
  onChange?: (isChecked: boolean) => void;
  ariaLabel: string;
  children: ReactNode;
};

export function TriCheckbox({ state, onChange, ariaLabel, children }: TriCheckboxProps) {
  const checkboxRef = useRef<HTMLInputElement>(null);
  useEffect(() => {
    if (checkboxRef && checkboxRef.current) {
      checkboxRef.current.indeterminate = state === "some";
    }
  }, [checkboxRef, state]);

  return (
    <div className="flex flex-row items-center">
      <div className="w-[45px] min-w-[45px] text-center">
        <Checkbox
          ref={checkboxRef}
          className="checkbox-sm checkbox-primary"
          checked={state === "all" || state === true}
          onChange={(e) => onChange && onChange(e.target.checked)}
          aria-label={ariaLabel}
        />
      </div>
      <div className="min-w-0 flex-1">{children}</div>
    </div>
  );
}
