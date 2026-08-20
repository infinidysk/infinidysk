import { useCallback, useRef, useEffect, type ChangeEvent } from "react";
import { Textarea } from "~/components/ui/form";
import { classNames } from "~/utils/styling";

type ExpandingTextInputProps = {
  className?: string;
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
  id?: string;
  "aria-describedby"?: string;
  readOnly?: boolean;
};

export function ExpandingTextInput({
  className,
  value,
  onChange,
  placeholder,
  id,
  "aria-describedby": ariaDescribedBy,
  readOnly,
}: ExpandingTextInputProps) {
  const textareaRef = useRef<HTMLTextAreaElement>(null);

  const adjustHeight = useCallback(() => {
    const textarea = textareaRef.current;
    if (textarea) {
      textarea.style.height = "0";
      textarea.style.height = `${textarea.scrollHeight}px`;
    }
  }, []);

  // Adjust height when value changes, becoming visible, or container resizes
  useEffect(() => {
    const textarea = textareaRef.current;
    if (!textarea) return;

    adjustHeight();

    const intersectionObserver = new IntersectionObserver((entries) => {
      if (entries[0]?.isIntersecting) {
        adjustHeight();
      }
    });
    intersectionObserver.observe(textarea);

    const resizeObserver = new ResizeObserver(() => {
      adjustHeight();
    });
    resizeObserver.observe(textarea);

    return () => {
      intersectionObserver.disconnect();
      resizeObserver.disconnect();
    };
  }, [value, adjustHeight]);

  const handleChange = useCallback(
    (e: ChangeEvent<HTMLTextAreaElement>) => {
      onChange(e.target.value);
    },
    [onChange],
  );

  return (
    <Textarea
      ref={textareaRef}
      className={classNames(["min-h-[2.25rem] w-full resize-none overflow-hidden", className])}
      value={value}
      onChange={handleChange}
      onInput={adjustHeight}
      placeholder={placeholder}
      id={id}
      aria-describedby={ariaDescribedBy}
      readOnly={readOnly}
      spellCheck={false}
    />
  );
}
