import { forwardRef } from "react";
import type {
  FieldsetHTMLAttributes,
  HTMLAttributes,
  InputHTMLAttributes,
  LabelHTMLAttributes,
  ReactNode,
  SelectHTMLAttributes,
  TextareaHTMLAttributes,
} from "react";

export function Field({ className = "", ...props }: FieldsetHTMLAttributes<HTMLFieldSetElement>) {
  return <fieldset className={`fieldset ${className}`} {...props} />;
}

export function Label({ className = "", ...props }: LabelHTMLAttributes<HTMLLabelElement>) {
  return <label className={`fieldset-legend text-sm font-medium text-base-content ${className}`} {...props} />;
}

export function HelpText({
  className = "",
  muted: _muted,
  ...props
}: HTMLAttributes<HTMLElement> & { muted?: boolean }) {
  return (
    <small
      className={`block text-[11px] leading-relaxed text-base-content/45 ${className}`}
      {...props}
    />
  );
}

export const Input = forwardRef<HTMLInputElement, InputHTMLAttributes<HTMLInputElement>>(
  function Input({ className = "", ...props }, ref) {
    return <input ref={ref} className={`input ${className}`} {...props} />;
  },
);

export const Select = forwardRef<HTMLSelectElement, SelectHTMLAttributes<HTMLSelectElement>>(
  function Select({ className = "", ...props }, ref) {
    return <select ref={ref} className={`select ${className}`} {...props} />;
  },
);

export const Textarea = forwardRef<HTMLTextAreaElement, TextareaHTMLAttributes<HTMLTextAreaElement>>(
  function Textarea({ className = "", ...props }, ref) {
    return <textarea ref={ref} className={`textarea ${className}`} {...props} />;
  },
);

export const Checkbox = forwardRef<HTMLInputElement, InputHTMLAttributes<HTMLInputElement>>(
  function Checkbox({ className = "", ...props }, ref) {
    return (
      <input
        ref={ref}
        type="checkbox"
        className={`checkbox ${className}`}
        {...props}
      />
    );
  },
);

type ToggleProps = Omit<InputHTMLAttributes<HTMLInputElement>, "type"> & {
  label: ReactNode;
};

export const Toggle = forwardRef<HTMLInputElement, ToggleProps>(function Toggle(
  { className = "", label, id, disabled, style, ...props },
  ref,
) {
  return (
    <label
      htmlFor={id}
      style={style}
      className={`label ${className}`}
    >
      <input ref={ref} id={id} type="checkbox" disabled={disabled} className="toggle toggle-success" {...props} />
      <span>{label}</span>
    </label>
  );
});

type CheckProps = Omit<InputHTMLAttributes<HTMLInputElement>, "type"> & {
  label: ReactNode;
  type?: "checkbox" | "radio" | "switch";
};

export const Check = forwardRef<HTMLInputElement, CheckProps>(function Check(
  { type = "checkbox", label, className = "", style, ...props },
  ref,
) {
  if (type === "switch") {
    return <Toggle ref={ref} label={label} className={className} style={style} {...props} />;
  }

  return (
    <label
      htmlFor={props.id}
      style={style}
      className={`label ${className}`}
    >
      <input
        ref={ref}
        type={type}
        className={type === "radio" ? "radio" : "checkbox"}
        {...props}
      />
      <span>{label}</span>
    </label>
  );
});

export const NativeForm = {
  Group: Field,
  Label,
  Select,
  Control: Input,
  Check,
  Text: HelpText,
};
