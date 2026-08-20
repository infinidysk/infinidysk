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
  xsmall: "btn-xs",
  small: "btn-sm",
  medium: "",
  large: "btn-lg",
  rounded: "btn-circle",
};

export type ButtonProps = ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: ButtonVariant;
  size?: ButtonSize;
};

export const Button = forwardRef<HTMLButtonElement, ButtonProps>(function Button(
  { variant = "primary", size = "small", className = "", type = "button", ...props },
  ref,
) {
  return (
    <button
      ref={ref}
      type={type}
      className={`btn gap-2 ${variants[variant]} ${sizes[size]} ${className}`}
      {...props}
    />
  );
});
