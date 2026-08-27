import { useEffect, useRef, type CSSProperties, type ReactNode } from "react";

export type DropdownOptionsProps = {
  className?: string;
  style?: CSSProperties;
  isOpen?: boolean;
  options: (DropdownOption | undefined)[];
  onClose?: () => void;
};

export type DropdownOption = {
  option: ReactNode;
  variant?: undefined | "danger";
  linkTo?: string;
  onSelect?: () => void;
};

export function DropdownOptions({
  className,
  style,
  isOpen = true,
  options,
  onClose,
}: DropdownOptionsProps) {
  const ref = useRef<HTMLUListElement>(null);

  useEffect(() => {
    if (!isOpen) return;

    function handleClick(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) {
        onClose?.();
      }
    }

    function handleKey(e: KeyboardEvent) {
      if (e.key === "Escape") onClose?.();
    }

    document.addEventListener("click", handleClick);
    document.addEventListener("keydown", handleKey);
    return () => {
      document.removeEventListener("click", handleClick);
      document.removeEventListener("keydown", handleKey);
    };
  }, [isOpen, onClose]);

  return !isOpen ? null : (
    <ul
      ref={ref}
      tabIndex={0}
      className={`menu dropdown-content absolute right-0 top-full z-50 mt-1 w-52 rounded-box border border-base-content/10 bg-base-200 p-2 shadow-lg ${className ?? ""}`}
      style={style}
    >
      {options
        .filter((x) => !!x)
        .map((option, index) => (
          <li key={index}>
            {option.linkTo ? (
              <a
                href={option.linkTo}
                className={option.variant === "danger" ? "text-error" : undefined}
                onClick={() => {
                  option.onSelect?.();
                  onClose?.();
                }}
              >
                {option.option}
              </a>
            ) : (
              <button
                type="button"
                className={option.variant === "danger" ? "text-error" : undefined}
                onClick={() => {
                  option.onSelect?.();
                  onClose?.();
                }}
              >
                {option.option}
              </button>
            )}
          </li>
        ))}
    </ul>
  );
}
