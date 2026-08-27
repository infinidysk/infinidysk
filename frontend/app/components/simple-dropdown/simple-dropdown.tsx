import { memo, useCallback, useState, type ChangeEvent, type RefObject } from "react";
import { Select } from "~/components/ui";

export type SimpleDropdownProps = {
  type?: "plain" | "bordered";
  options: string[];
  /** Optional display labels keyed by option value (values stay in `options`). */
  optionLabels?: Record<string, string>;
  value?: string;
  onChange?: (value: string) => void;
  valueRef?: RefObject<string>;
  ariaLabel?: string;
};

export const SimpleDropdown = memo(
  ({ type, options, optionLabels, value, onChange, valueRef, ariaLabel }: SimpleDropdownProps) => {
    if (!valueRef && (value == null || !onChange)) {
      throw new Error(
        "SimpleDropdown requires either the valueRef prop or both the value and onChange props.",
      );
    }

    const [internalValue, setInternalValue] = useState(
      () => valueRef?.current ?? (options.length > 0 ? options[0] : ""),
    );
    const renderedValue = value ?? internalValue;

    const handleNativeChange = useCallback(
      (e: ChangeEvent<HTMLSelectElement>) => {
        const next = e.target.value;
        if (valueRef) {
          setInternalValue(next);
          valueRef.current = next;
        }
        onChange?.(next);
      },
      [valueRef, onChange],
    );

    return (
      <Select
        aria-label={ariaLabel ?? "Select option"}
        className={`select-xs w-auto min-w-20 ${type === "bordered" ? "" : "select-ghost"}`.trim()}
        value={renderedValue}
        onChange={handleNativeChange}
      >
        {options.map((option) => (
          <option key={option} value={option}>
            {optionLabels?.[option] ?? option}
          </option>
        ))}
      </Select>
    );
  },
);
