type RadioJoinOption<T extends string> = {
  id: T;
  label: string;
};

/** Persistent single-select filter using native radios styled as daisyUI join buttons. */
export function RadioJoinFilter<T extends string>({
  name,
  value,
  options,
  onChange,
  "aria-label": ariaLabel,
}: {
  name: string;
  value: T;
  options: readonly RadioJoinOption<T>[];
  onChange: (value: T) => void;
  "aria-label"?: string;
}) {
  return (
    <div className="join flex-wrap" role="radiogroup" aria-label={ariaLabel ?? name}>
      {options.map((option) => (
        <input
          key={option.id}
          type="radio"
          name={name}
          className="btn btn-sm join-item max-sm:min-h-11"
          aria-label={option.label}
          checked={value === option.id}
          onChange={() => onChange(option.id)}
        />
      ))}
    </div>
  );
}
