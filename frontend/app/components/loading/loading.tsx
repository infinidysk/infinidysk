import { Spinner } from "~/components/ui";

export type LoadingProps = {
    className?: string
}

export function Loading({ className }: LoadingProps) {
    return (
        <div className={`flex min-h-[50dvh] w-full flex-col items-center justify-center gap-3 text-base-content/60 ${className ?? ""}`}>
            <Spinner className="loading-lg text-primary" />
            <div className="text-sm font-medium">Loading...</div>
        </div>
    );
}
