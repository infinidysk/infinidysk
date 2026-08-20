import type { ReactNode } from "react";
import { Badge } from "~/components/ui";

export type PageTableProps = {
  title: ReactNode;
  subTitle?: ReactNode;
  badgeText?: string;
  children?: ReactNode;
};

export function PageSection({ title, subTitle, badgeText, children }: PageTableProps) {
  return (
    <section className="card card-border bg-base-100 w-full shadow-md">
      <div className="card-body gap-4 pb-0 sm:pb-0">
        <div>
          <div className="mb-2.5 flex flex-wrap items-center justify-between gap-4">
            {title}
            {badgeText && (
              <Badge className="badge-ghost badge-sm font-mono tabular-nums">{badgeText}</Badge>
            )}
          </div>
          {subTitle}
        </div>
        {children}
      </div>
    </section>
  );
}
