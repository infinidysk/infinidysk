export function ProfileAvatar({ name, className }: { name: string; className?: string }) {
  const initial = name.trim().charAt(0).toUpperCase() || "?";
  return (
    <span
      className={`flex items-center justify-center rounded-full bg-primary font-semibold text-primary-content ${className ?? "h-8 w-8 text-sm"}`}
    >
      {initial}
    </span>
  );
}
