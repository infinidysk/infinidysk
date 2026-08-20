import type { ReactNode } from "react";

export type TruncateProps = {
    children: ReactNode
}

export function Truncate({ children }: TruncateProps) {
    return (
        <div className="line-clamp-1 break-all whitespace-normal active:line-clamp-[10] max-[899px]:line-clamp-[10]">
            {children}
        </div>
    );
}