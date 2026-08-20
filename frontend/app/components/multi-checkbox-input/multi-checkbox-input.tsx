import { useCallback, useMemo } from "react";
import { Checkbox } from "~/components/ui/form";

type MultiCheckboxInputProps = {
  options: string[];
  value: string;
  onChange: (value: string) => void;
};

export function MultiCheckboxInput({ options, value, onChange }: MultiCheckboxInputProps) {
  const selectedOptions = useMemo(() => {
    if (!value || value.trim() === "") return [];
    return value
      .split(",")
      .map((c) => c.trim())
      .filter((c) => c.length > 0);
  }, [value]);

  const onOptionCheckboxChange = useCallback(
    (option: string, checked: boolean) => {
      let newSelected: string[];
      if (checked) {
        newSelected = [...selectedOptions, option];
      } else {
        newSelected = selectedOptions.filter((o) => o !== option);
      }
      onChange(newSelected.join(", "));
    },
    [onChange, selectedOptions],
  );

  if (options.length === 0) {
    return null;
  }

  return (
    <div className="mt-3 grid grid-cols-1 gap-2 rounded border border-base-content/10 bg-base-200/30 p-3 sm:grid-cols-2">
      {options.map((option) => (
        <label key={option} className="flex items-center gap-2 text-sm text-base-content/80">
          <Checkbox
            id={`multi-checkbox-${option}`}
            checked={selectedOptions.includes(option)}
            onChange={(e) => onOptionCheckboxChange(option, e.target.checked)}
          />
          <span>{option}</span>
        </label>
      ))}
    </div>
  );
}
