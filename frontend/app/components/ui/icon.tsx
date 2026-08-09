import type { HTMLAttributes } from "react";

type IconProps = HTMLAttributes<HTMLSpanElement> & {
  name: string;
  filled?: boolean | undefined;
};

export function Icon({ name, filled = false, className = "", style, ...props }: IconProps) {
  return (
    <span
      aria-hidden="true"
      className={`material-symbols-rounded select-none leading-none ${className}`}
      style={{
        fontVariationSettings: `'FILL' ${filled ? 1 : 0}, 'wght' 300, 'GRAD' 0, 'opsz' 24`,
        ...style,
      }}
      {...props}
    >
      {name}
    </span>
  );
}
