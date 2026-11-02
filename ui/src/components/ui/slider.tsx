import * as React from "react"
import { cn } from "cn"
import { Slider as SliderPrimitive } from "radix-ui"
import { useField } from "@/lib/field"

function Slider({ className, "aria-label": label, ...props }: React.ComponentProps<typeof SliderPrimitive.Root>) {
  const thumbs = props.value ?? props.defaultValue ?? [0]
  const field = useField()
  return (
    <SliderPrimitive.Root
      data-slot="slider"
      className={cn(
        "relative flex w-full touch-none items-center py-1.5 select-none data-disabled:cursor-not-allowed data-disabled:opacity-50",
        className
      )}
      {...props}
    >
      <SliderPrimitive.Track data-slot="slider-track" className="relative h-1.5 w-full grow overflow-hidden rounded-full bg-muted">
        <SliderPrimitive.Range data-slot="slider-range" className="absolute h-full bg-primary" />
      </SliderPrimitive.Track>
      {thumbs.map((_, i) => (
        <SliderPrimitive.Thumb
          key={i}
          {...field}
          aria-label={label}
          data-slot="slider-thumb"
          className="relative block size-4 shrink-0 rounded-full border border-primary bg-background shadow-sm transition-[color,box-shadow] outline-none after:absolute after:-inset-3 hover:ring-3 hover:ring-ring/50 focus-visible:ring-3 focus-visible:ring-ring/50"
        />
      ))}
    </SliderPrimitive.Root>
  )
}

export { Slider }
