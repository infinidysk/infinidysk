import { Icon } from "./icon";

type RadioJoinOption<T extends string> = {
  id: T;
  label: string;
  description?: string;
  icon?: string;
};

/** Persistent single-select filter using native radios styled as daisyUI join buttons. */
export function RadioJoinFilter<T extends string>({
  name,
  value,
  options,
  onChange,
  prominent = false,
  "aria-label": ariaLabel,
}: {
  name: string;
  value: T;
  options: readonly RadioJoinOption<T>[];
  onChange: (value: T) => void;
  prominent?: boolean;
  "aria-label"?: string;
}) {
  return (
    <div
      className={prominent ? "grid w-full grid-cols-1 gap-3 sm:grid-cols-2" : "join flex-wrap"}
      role="radiogroup"
      aria-label={ariaLabel ?? name}
    >
      {options.map((option) => {
        const selected = value === option.id;
        const descriptionId = `${name}-${option.id}-description`;
        return (
          <label
            key={option.id}
            className={
              prominent
                ? `flex min-w-0 cursor-pointer items-start gap-3 rounded-box border p-4 text-left transition-colors focus-within:outline-2 focus-within:outline-offset-2 focus-within:outline-primary ${
                    selected
                      ? "border-primary/60 bg-primary/10"
                      : "border-base-content/10 bg-base-200/30 hover:border-base-content/25"
                  }`
                : `btn btn-sm join-item max-sm:min-h-11 max-sm:px-2.5 focus-within:outline-2 focus-within:outline-offset-2 focus-within:outline-primary ${
                    selected ? "btn-primary" : "btn-ghost"
                  }`
            }
          >
            <input
              type="radio"
              name={name}
              aria-label={option.label}
              aria-describedby={prominent && option.description ? descriptionId : undefined}
              className={prominent ? "radio radio-sm radio-primary mt-0.5 shrink-0" : "sr-only"}
              checked={selected}
              onChange={() => onChange(option.id)}
            />
            {prominent ? (
              <>
                {option.icon && (
                  <Icon
                    name={option.icon}
                    filled={selected}
                    className={`!text-[20px] shrink-0 ${selected ? "text-primary" : "text-base-content/70"}`}
                  />
                )}
                <span className="min-w-0">
                  <span className="block text-sm font-semibold">{option.label}</span>
                  {option.description && (
                    <span
                      id={descriptionId}
                      className="mt-1 block text-xs leading-relaxed text-base-content/60"
                    >
                      {option.description}
                    </span>
                  )}
                </span>
              </>
            ) : (
              <span>{option.label}</span>
            )}
          </label>
        );
      })}
    </div>
  );
}
