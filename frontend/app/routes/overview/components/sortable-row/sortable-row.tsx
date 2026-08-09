import type { ReactNode } from "react";
import { useSortable } from "@dnd-kit/sortable";
import { CSS } from "@dnd-kit/utilities";

type Props = {
  id: string;
  editMode: boolean;
  children: ReactNode;
};

export function SortableRow({ id, editMode, children }: Props) {
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({
    id,
    disabled: !editMode,
  });

  const style = {
    transform: CSS.Transform.toString(transform),
    transition,
  };

  const rowClass = [
    "relative",
    editMode
      ? "rounded-box border border-dashed border-base-content/20 bg-base-200 py-2 pr-2 pl-10 transition-[border-color,background] duration-100 hover:border-primary/50"
      : "",
    isDragging ? "z-[1] opacity-60" : "",
  ]
    .filter(Boolean)
    .join(" ");

  return (
    <div ref={setNodeRef} style={style} className={rowClass}>
      {editMode && (
        <button
          type="button"
          className="btn btn-ghost btn-xs absolute top-1/2 left-2 h-8 w-6 -translate-y-1/2 cursor-grab touch-none p-0 text-base-content/50 hover:bg-base-100 hover:text-base-content active:cursor-grabbing"
          aria-label="Drag to reorder"
          {...attributes}
          {...listeners}
        >
          <svg width="14" height="14" viewBox="0 0 16 16" fill="currentColor" aria-hidden="true">
            <circle cx="5" cy="3" r="1.4" />
            <circle cx="11" cy="3" r="1.4" />
            <circle cx="5" cy="8" r="1.4" />
            <circle cx="11" cy="8" r="1.4" />
            <circle cx="5" cy="13" r="1.4" />
            <circle cx="11" cy="13" r="1.4" />
          </svg>
        </button>
      )}
      <div className="min-w-0">{children}</div>
    </div>
  );
}
