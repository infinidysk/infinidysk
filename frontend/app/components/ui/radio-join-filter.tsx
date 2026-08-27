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
      {options.map((option) => {
        const selected = value === option.id;
        return (
          <label
            key={option.id}
            className={`btn btn-sm join-item max-sm:min-h-11 focus-within:outline-2 focus-within:outline-offset-2 focus-within:outline-primary ${
              selected ? "btn-active" : "btn-ghost"
            }`}
          >
            <input
              type="radio"
              name={name}
              className="sr-only"
              checked={selected}
              onChange={() => onChange(option.id)}
            />
            <span>{option.label}</span>
          </label>
        );
      })}
    </div>
  );
}
