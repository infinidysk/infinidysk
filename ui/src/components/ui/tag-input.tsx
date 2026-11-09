import * as React from "react"
import { X } from "lucide-react"
import { cn } from "@/lib/utils"

/** Chips with a trailing input; Enter, comma or space adds, Backspace on empty removes the last. */
function TagInput({
  id,
  value,
  disabled,
  onChange,
}: {
  id: string
  value: string[]
  disabled: boolean
  onChange: (value: string[]) => void
}) {
  const [text, setText] = React.useState("")
  const add = () => {
    if (text && !value.includes(text)) onChange([...value, text])
    setText("")
  }
  return (
    <div
      data-slot="tag-input"
      className={cn(
        "flex min-h-8 w-full flex-wrap items-center gap-1.5 rounded-lg border border-input px-1.5 py-1 transition-colors focus-within:border-ring focus-within:ring-3 focus-within:ring-ring/50 dark:bg-input/30",
        disabled && "pointer-events-none cursor-not-allowed opacity-50"
      )}
    >
      {value.map((tag) => (
        <span key={tag} className="flex h-6 items-center gap-1 rounded-full border border-input pr-1 pl-2.5 text-sm">
          {tag}
          <button
            type="button"
            aria-label={`Remove ${tag}`}
            className="-my-1 grid size-6 place-items-center rounded-full text-muted-foreground hover:text-foreground"
            onClick={() => onChange(value.filter((t) => t !== tag))}
          >
            <X className="size-3.5" />
          </button>
        </span>
      ))}
      <input
        id={id}
        disabled={disabled}
        value={text}
        onChange={(e) => {
          // Android keyboards report commas and spaces as "Unidentified" keys, so split here too.
          const parts = e.target.value.split(/[\s,]+/).map((p) => p.replace(/[^A-Za-z0-9-]/g, ""))
          const rest = parts.pop() ?? ""
          const added = parts.filter((p) => p && !value.includes(p))
          if (added.length) onChange([...value, ...new Set(added)])
          setText(rest)
        }}
        onBlur={add}
        onKeyDown={(e) => {
          if (e.key === "Enter" || e.key === "," || e.key === " ") {
            e.preventDefault()
            add()
          } else if (e.key === "Backspace" && !text && value.length) onChange(value.slice(0, -1))
        }}
        className="h-6 min-w-24 flex-1 bg-transparent px-1 text-sm outline-none"
      />
    </div>
  )
}

export { TagInput }
