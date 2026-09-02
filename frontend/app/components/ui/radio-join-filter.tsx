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
      className={
        prominent
          ? "join join-vertical w-full rounded-box border border-base-content/15 bg-base-200 p-1 sm:join-horizontal"
          : "join flex-wrap"
      }
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
                ? `btn join-item h-auto min-h-24 flex-1 justify-start gap-3 whitespace-normal p-4 text-left focus-within:outline-2 focus-within:outline-offset-2 focus-within:outline-primary ${
                    selected
                      ? "btn-primary shadow-md"
                      : "border border-transparent bg-base-100 text-base-content hover:border-base-content/20 hover:bg-base-100"
                  }`
                : `btn btn-sm join-item max-sm:min-h-11 focus-within:outline-2 focus-within:outline-offset-2 focus-within:outline-primary ${
                    selected ? "btn-active" : "btn-ghost"
                  }`
            }
          >
            <input
              type="radio"
              name={name}
              aria-label={option.label}
              aria-describedby={prominent && option.description ? descriptionId : undefined}
              className={prominent ? "radio radio-sm shrink-0" : "sr-only"}
              checked={selected}
              onChange={() => onChange(option.id)}
            />
            {prominent ? (
              <>
                {option.icon && <Icon name={option.icon} className="!text-[24px] shrink-0" />}
                <span className="min-w-0">
                  <span className="block text-base font-semibold">{option.label}</span>
                  {option.description && (
                    <span
                      id={descriptionId}
                      className="mt-1 block text-xs font-normal leading-relaxed opacity-75"
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
