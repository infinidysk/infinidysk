import type { ReactNode } from "react";

export type WordWrapProps = {
  children: ReactNode;
};

export function WordWrap({ children }: WordWrapProps) {
  return <div className="min-w-0 break-words">{children}</div>;
}
