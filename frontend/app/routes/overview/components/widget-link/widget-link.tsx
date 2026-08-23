import { Link } from "react-router";

export function WidgetLink({ to, children }: { to: string; children: string }) {
  return (
    <Link to={to} className="link link-hover shrink-0 text-xs font-normal text-base-content/60">
      {children}
    </Link>
  );
}
