import { forwardRef } from "react";
import type { ButtonHTMLAttributes } from "react";

type ButtonVariant =
  "primary" | "success" | "danger" | "warning" | "secondary" | "outline" | "ghost";
type ButtonSize = "xsmall" | "small" | "medium" | "large" | "rounded";

const variants: Record<ButtonVariant, string> = {
  primary: "btn-primary",
  success: "btn-success",
  danger: "btn-error",
  warning: "btn-warning",
  secondary: "btn-neutral",
  outline: "btn-outline",
  ghost: "btn-ghost",
};

const sizes: Record<ButtonSize, string> = {
  xsmall: "btn-xs max-sm:min-h-11",
  small: "btn-sm max-sm:min-h-11",
  medium: "",
  large: "btn-lg",
  rounded: "btn-circle max-sm:min-h-11 max-sm:min-w-11",
};

export type ButtonProps = ButtonHTMLAttributes<HTMLButtonElement> & {
  /** Omit for the default daisyUI `btn` (no color). Use `primary` only for the page CTA. */
  variant?: ButtonVariant;
  size?: ButtonSize;
};

export const Button = forwardRef<HTMLButtonElement, ButtonProps>(function Button(
  { variant, size = "small", className = "", type = "button", ...props },
  ref,
) {
  return (
    <button
      ref={ref}
      type={type}
      className={`btn gap-2 ${variant ? variants[variant] : ""} ${sizes[size]} ${className}`}
      {...props}
    />
  );
});
